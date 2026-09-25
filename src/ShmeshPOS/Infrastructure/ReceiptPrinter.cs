using System.Printing;
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
