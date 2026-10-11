using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;

namespace ShmeshPOS.Services;

public interface IReceiptService
{
    /// <summary>The receipt as text lines, for on-screen preview.</summary>
    Task<IReadOnlyList<string>> FormatSaleAsync(long saleId, CancellationToken cancellationToken = default);

    /// <summary>Prints a sale's receipt on this register's receipt printer.</summary>
    Task PrintSaleAsync(long saleId, CancellationToken cancellationToken = default);

    /// <summary>Prints a sales / end-of-day report on this register's receipt printer.</summary>
    Task PrintSummaryAsync(SalesSummary summary, CancellationToken cancellationToken = default);

    /// <summary>Whether this register prints a receipt automatically after every sale.</summary>
    Task<bool> IsAutoPrintAsync(CancellationToken cancellationToken = default);

    Task PrintRefundAsync(long refundId, CancellationToken cancellationToken = default);

    /// <summary>The shift report as text lines, for on-screen preview.</summary>
    IReadOnlyList<string> FormatShift(ShiftReport report);

    /// <summary>Prints a shift (drawer) report on this register's receipt printer.</summary>
    Task PrintShiftAsync(ShiftReport report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the cash drawer if one is connected to this register's printer. Returns false when no drawer is
    /// set up. Every opening is logged with its reason.
    /// </summary>
    Task<bool> OpenDrawerAsync(string reason, CancellationToken cancellationToken = default);
}

public class ReceiptService(
    IReportService reports,
    IRefundService refunds,
    ISettingsService settings,
    IReceiptPrinter printer,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<ReceiptService> logger) : IReceiptService
{
    public async Task<IReadOnlyList<string>> FormatSaleAsync(long saleId, CancellationToken cancellationToken = default)
    {
        var receipt = await reports.GetReceiptAsync(saleId, cancellationToken);
        var text = await settings.GetReceiptSettingsAsync(cancellationToken);
        return ReceiptFormatter.Receipt(receipt, text);
    }

    public async Task PrintSaleAsync(long saleId, CancellationToken cancellationToken = default)
    {
        var lines = await FormatSaleAsync(saleId, cancellationToken);
        var print = await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken);
        printer.Print(lines, print.PrinterName, $"Receipt {saleId}");
        logger.LogInformation("Printed receipt for sale {SaleId} on {Printer}", saleId, print.PrinterName ?? "default printer");
    }

    public async Task PrintSummaryAsync(SalesSummary summary, CancellationToken cancellationToken = default)
    {
        var lines = ReceiptFormatter.Summary(summary, options.Value.StoreName, session.CurrentUser?.DisplayName ?? "",
            clock.GetLocalNow().DateTime);
        var print = await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken);
        printer.Print(lines, print.PrinterName, "Sales report");
        logger.LogInformation("Printed sales report {From} – {To} for user {UserId}", summary.From, summary.To, session.CurrentUser?.Id);
    }

    public async Task PrintRefundAsync(long refundId, CancellationToken cancellationToken = default)
    {
        var receipt = await refunds.GetRefundReceiptAsync(refundId, cancellationToken);
        var text = await settings.GetReceiptSettingsAsync(cancellationToken);
        var print = await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken);
        printer.Print(ReceiptFormatter.Receipt(receipt, text), print.PrinterName, $"Refund {refundId}");
    }

    public IReadOnlyList<string> FormatShift(ShiftReport report) =>
        ReceiptFormatter.Shift(report, options.Value.StoreName, session.CurrentUser?.DisplayName ?? "", clock.GetLocalNow().DateTime);

    public async Task PrintShiftAsync(ShiftReport report, CancellationToken cancellationToken = default)
    {
        var print = await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken);
        printer.Print(FormatShift(report), print.PrinterName, $"Shift {report.ShiftId}");
        logger.LogInformation("Printed shift report {ShiftId} for user {UserId}", report.ShiftId, session.CurrentUser?.Id);
    }

    public async Task<bool> OpenDrawerAsync(string reason, CancellationToken cancellationToken = default)
    {
        var print = await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken);
        if (!print.DrawerConnected || print.PrinterName is null) return false;
        printer.OpenDrawer(print.PrinterName);
        logger.LogInformation("Cash drawer opened on {Terminal} by user {UserId}: {Reason}",
            options.Value.TerminalName, session.CurrentUser?.Id, reason);
        return true;
    }

    public async Task<bool> IsAutoPrintAsync(CancellationToken cancellationToken = default) =>
        (await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken)).AutoPrint;
}
