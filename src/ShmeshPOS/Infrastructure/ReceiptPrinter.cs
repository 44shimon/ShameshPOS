using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ShmeshPOS.Services;

namespace ShmeshPOS.Infrastructure;

/// <summary>Sends fixed-width text to a Windows printer (normally an 80 mm thermal receipt printer).</summary>
public interface IReceiptPrinter
{
    IReadOnlyList<string> InstalledPrinters();

    /// <param name="printerName">A Windows printer name, or null for the Windows default printer.</param>
    void Print(IReadOnlyList<string> lines, string? printerName, string jobName);

    /// <summary>Sends the ESC/POS "open cash drawer" pulse to the receipt printer the drawer is plugged into.</summary>
    void OpenDrawer(string printerName);
}

public class ReceiptPrinter : IReceiptPrinter
{
    // 80 mm roll with ~72 mm printable width ≈ 272 device-independent pixels (1/96 inch).
    private const double PageWidth = 280;
    private const double FontSize = 11.5;
    private const double LineHeight = 15;

    public IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName).OrderBy(n => n).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public void Print(IReadOnlyList<string> lines, string? printerName, string jobName)
    {
        var dialog = new PrintDialog();
        try
        {
            if (printerName is not null)
            {
                var server = new LocalPrintServer();
                dialog.PrintQueue = server.GetPrintQueue(printerName);
            }
        }
        catch (Exception ex)
        {
            throw new PosException($"The receipt printer “{printerName}” was not found. Check it in Administration → Settings.", ex);
        }

        var document = BuildDocument(lines);
        try
        {
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, jobName);
        }
        catch (Exception ex)
        {
            throw new PosException("The receipt could not be printed. Check that the printer is on and has paper.", ex);
        }
    }

    // ESC p m t1 t2: pulse drawer pin 2 for 25 x 2 ms on, 250 x 2 ms off. Understood by Epson, Star, Bixolon and most clones.
    private static readonly byte[] DrawerKick = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    public void OpenDrawer(string printerName)
    {
        if (!RawPrinter.Send(printerName, DrawerKick, "Open drawer"))
            throw new PosException($"The cash drawer did not open. Check that “{printerName}” is on and the drawer cable is plugged into it.");
    }

    /// <summary>One continuous page as long as the receipt, so roll printers cut once at the end.</summary>
    internal static FlowDocument BuildDocument(IReadOnlyList<string> lines)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = LineHeight };
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            // Spaces are significant (column alignment), so keep them as non-breaking.
            paragraph.Inlines.Add(new Run(lines[i].Replace(' ', ' ')));
        }

        return new FlowDocument(paragraph)
        {
            FontFamily = new FontFamily("Consolas, Courier New"),
            FontSize = FontSize,
            PageWidth = PageWidth,
            ColumnWidth = PageWidth,
            PagePadding = new Thickness(4, 8, 4, 24),
            PageHeight = Math.Max(200, lines.Count * LineHeight + 60),
        };
    }
}

/// <summary>Writes bytes straight to a printer (RAW datatype), bypassing the driver's page rendering.</summary>
internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        public string? DocName;
        public string? OutputFile;
        public string? DataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    public static bool Send(string printerName, byte[] bytes, string documentName)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) return false;
        try
        {
            if (StartDocPrinter(handle, 1, new DocInfo { DocName = documentName, DataType = "RAW" }) == 0) return false;
            try
            {
                if (!StartPagePrinter(handle)) return false;
                var ok = WritePrinter(handle, bytes, bytes.Length, out var written) && written == bytes.Length;
                EndPagePrinter(handle);
                return ok;
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }
}
