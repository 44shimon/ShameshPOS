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
}

public class ReceiptService(
    IReportService reports,
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

    public async Task<bool> IsAutoPrintAsync(CancellationToken cancellationToken = default) =>
        (await settings.GetPrintSettingsAsync(options.Value.TerminalName, cancellationToken)).AutoPrint;
}
