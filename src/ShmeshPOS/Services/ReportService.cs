using Microsoft.EntityFrameworkCore;
using ShmeshPOS.Data;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public sealed record NamedTotal(string Name, int Count, decimal Amount);

public sealed record ProductSales(string Name, int Quantity, decimal Revenue);

/// <summary>Everything on the sales / end-of-day report for a period [From, To) in local time.</summary>
public sealed record SalesSummary(
    DateTime From,
    DateTime To,
    int SaleCount,
    int ItemsSold,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    IReadOnlyList<NamedTotal> ByPaymentMethod,
    IReadOnlyList<NamedTotal> ByCashier,
    IReadOnlyList<ProductSales> TopProducts,
    decimal AccountCharges,
    IReadOnlyList<NamedTotal> AccountPaymentsByMethod,
    decimal CashSales,
    decimal CashAccountPayments)
{
    public decimal AverageSale => SaleCount == 0 ? 0m : SaleCalculator.RoundMoney(Total / SaleCount);
    public decimal AccountPaymentsTotal => AccountPaymentsByMethod.Sum(p => p.Amount);

    /// <summary>Cash that should have gone into the drawer: cash sales plus cash paid onto accounts (opening float not included).</summary>
    public decimal ExpectedCash => CashSales + CashAccountPayments;
}

public sealed record SaleListItem(
    long Id, string ReceiptNumber, DateTime Time, string Cashier, string? Customer, decimal Total, string PaidBy, string Terminal);

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record ReceiptPayment(string Method, decimal Amount, decimal? CashReceived, decimal? Change);

/// <summary>A completed sale as it appears on a printed receipt.</summary>
public sealed record ReceiptData(
    string ReceiptNumber,
    DateTime Time,
    string Cashier,
    string Terminal,
    string? CustomerName,
    string? CustomerAccount,
    decimal? CustomerBalanceAfter,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    IReadOnlyList<ReceiptPayment> Payments);

public interface IReportService
{
    /// <summary>Totals for completed sales from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), local time.</summary>
    Task<SalesSummary> GetSummaryAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>Completed sales in the period, newest first (up to <paramref name="max"/>).</summary>
    Task<IReadOnlyList<SaleListItem>> GetSalesAsync(DateTime from, DateTime to, int max = 1000, CancellationToken cancellationToken = default);

    /// <summary>Receipt contents for reprinting. Cashiers may reprint; they don't need report access.</summary>
    Task<ReceiptData> GetReceiptAsync(long saleId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only reporting over completed sales and customer-account payments. Figures come straight from the
/// saved sale, line and payment rows (names and prices as they were at the time of sale).
/// </summary>
public class ReportService(IDbContextFactory<PosDbContext> dbFactory, ISessionService session, TimeProvider clock) : IReportService
{
    public const int TopProductCount = 15;

    public async Task<SalesSummary> GetSummaryAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        Require(Permission.ViewReports, "view reports");
        var (fromUtc, toUtc) = ToUtcRange(from, to);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sales = db.Sales.AsNoTracking()
            .Where(s => s.Status == SaleStatus.Completed && s.CreatedAt >= fromUtc && s.CreatedAt < toUtc);

        var totals = await sales
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Subtotal = g.Sum(s => s.Subtotal), Tax = g.Sum(s => s.Tax), Total = g.Sum(s => s.Total) })
            .FirstOrDefaultAsync(cancellationToken);

        var byMethod = await db.Payments.AsNoTracking()
            .Where(p => sales.Any(s => s.Id == p.SaleId))
            .GroupBy(p => new { p.PaymentMethod!.Name, p.PaymentMethod.Kind, p.PaymentMethod.SortOrder })
            .Select(g => new { g.Key.Name, g.Key.Kind, g.Key.SortOrder, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        var byCashier = (await sales
                .GroupBy(s => s.Cashier!.DisplayName)
                .Select(g => new { Name = g.Key, Count = g.Count(), Amount = g.Sum(s => s.Total) })
                .OrderByDescending(x => x.Amount)
                .ToListAsync(cancellationToken))
            .Select(x => new NamedTotal(x.Name, x.Count, x.Amount))
            .ToList();

        var items = db.SaleItems.AsNoTracking().Where(i => sales.Any(s => s.Id == i.SaleId));
        var itemsSold = await items.SumAsync(i => (int?)i.Quantity, cancellationToken) ?? 0;
        var topProducts = (await items
                .GroupBy(i => i.ProductName)
                .Select(g => new { Name = g.Key, Quantity = g.Sum(i => i.Quantity), Revenue = g.Sum(i => i.LineTotal) })
                .OrderByDescending(x => x.Revenue).ThenByDescending(x => x.Quantity)
                .Take(TopProductCount)
                .ToListAsync(cancellationToken))
            .Select(x => new ProductSales(x.Name, x.Quantity, x.Revenue))
            .ToList();

        var ledger = db.CustomerTransactions.AsNoTracking().Where(t => t.CreatedAt >= fromUtc && t.CreatedAt < toUtc);
        var accountCharges = await ledger.Where(t => t.Type == CustomerTransactionType.Charge).SumAsync(t => (decimal?)t.Amount, cancellationToken) ?? 0m;
        var accountPayments = await ledger
            .Where(t => t.Type == CustomerTransactionType.Payment)
            .GroupBy(t => new { t.PaymentMethod!.Name, t.PaymentMethod.Kind, t.PaymentMethod.SortOrder })
            .Select(g => new { g.Key.Name, g.Key.Kind, g.Key.SortOrder, Count = g.Count(), Amount = -g.Sum(t => t.Amount) })
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        return new SalesSummary(
            from, to,
            totals?.Count ?? 0,
            itemsSold,
            totals?.Subtotal ?? 0m,
            totals?.Tax ?? 0m,
            totals?.Total ?? 0m,
            byMethod.Select(m => new NamedTotal(m.Name, m.Count, m.Amount)).ToList(),
            byCashier,
            topProducts,
            accountCharges,
            accountPayments.Select(p => new NamedTotal(p.Name, p.Count, p.Amount)).ToList(),
            byMethod.Where(m => m.Kind == PaymentKind.Cash).Sum(m => m.Amount),
            accountPayments.Where(p => p.Kind == PaymentKind.Cash).Sum(p => p.Amount));
    }

    public async Task<IReadOnlyList<SaleListItem>> GetSalesAsync(DateTime from, DateTime to, int max = 1000, CancellationToken cancellationToken = default)
    {
        Require(Permission.ViewReports, "view reports");
        var (fromUtc, toUtc) = ToUtcRange(from, to);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Sales.AsNoTracking()
            .Where(s => s.Status == SaleStatus.Completed && s.CreatedAt >= fromUtc && s.CreatedAt < toUtc)
            .OrderByDescending(s => s.CreatedAt)
            .Take(max)
            .Select(s => new
            {
                s.Id, s.ReceiptNumber, s.CreatedAt, Cashier = s.Cashier!.DisplayName, Customer = s.Customer!.Name,
                s.Total, s.TerminalName, Methods = s.Payments.Select(p => p.PaymentMethod!.Name).ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SaleListItem(r.Id, r.ReceiptNumber, ToLocal(r.CreatedAt), r.Cashier, r.Customer, r.Total,
            string.Join(", ", r.Methods), r.TerminalName)).ToList();
    }

    public async Task<ReceiptData> GetReceiptAsync(long saleId, CancellationToken cancellationToken = default)
    {
        if (!session.HasPermission(Permission.ViewReports) && !session.HasPermission(Permission.TakePayment))
            throw new PosException("You are not allowed to print receipts.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sale = await db.Sales.AsNoTracking()
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken)
            ?? throw new PosException("That sale could not be found.");

        var balanceAfter = await db.CustomerTransactions.AsNoTracking()
            .Where(t => t.SaleId == saleId && t.Type == CustomerTransactionType.Charge)
            .Select(t => (decimal?)t.BalanceAfter)
            .FirstOrDefaultAsync(cancellationToken);

        return new ReceiptData(
            sale.ReceiptNumber,
            ToLocal(sale.CreatedAt),
            sale.Cashier?.DisplayName ?? "",
            sale.TerminalName,
            sale.Customer?.Name,
            sale.Customer?.AccountNumber,
            balanceAfter,
            sale.Items.OrderBy(i => i.Id).Select(i => new ReceiptLine(i.ProductName, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
            sale.Subtotal,
            sale.Tax,
            sale.Total,
            sale.Payments.OrderBy(p => p.Id)
                .Select(p => new ReceiptPayment(p.PaymentMethod?.Name ?? "", p.Amount, p.CashReceived, p.ChangeGiven)).ToList());
    }

    private (DateTime FromUtc, DateTime ToUtc) ToUtcRange(DateTime from, DateTime to)
    {
        if (to <= from) throw new PosException("The end of the period must be after the start.");
        var zone = clock.LocalTimeZone;
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from, DateTimeKind.Unspecified), zone),
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(to, DateTimeKind.Unspecified), zone));
    }

    private DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone);

    private void Require(Permission permission, string action)
    {
        if (!session.HasPermission(permission))
            throw new PosException($"You are not allowed to {action}.");
    }
}
