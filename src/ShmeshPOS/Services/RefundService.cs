using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <param name="LineTotal">What was charged for the whole original line before tax, after any discount.</param>
/// <param name="TaxAmount">Tax charged on the whole original line.</param>
/// <param name="RefundedTotal">Line amount (before tax) already refunded.</param>
/// <param name="RefundedTax">Tax already refunded.</param>
public sealed record ReturnableLine(
    long SaleItemId, int ProductId, string Name, int Sold, int AlreadyReturned, decimal UnitPrice, decimal TaxRate, decimal LineTotal,
    decimal TaxAmount, decimal RefundedTotal, decimal RefundedTax)
{
    public int Remaining => Sold - AlreadyReturned;
}

public sealed record OriginalPayment(int PaymentMethodId, string Name, PaymentKind Kind, decimal Amount);

/// <summary>A completed sale as seen from the Returns screen: what was bought, and what is still returnable.</summary>
public sealed record ReturnableSale(
    long SaleId, string ReceiptNumber, DateTime Time, string Cashier, int? CustomerId, string? CustomerName, decimal Total,
    decimal AlreadyRefunded, IReadOnlyList<ReturnableLine> Lines, IReadOnlyList<OriginalPayment> Payments);

public sealed record RefundLineRequest(long SaleItemId, int Quantity);

/// <param name="Restock">Put the returned items back into stock (for products whose stock is tracked).</param>
public sealed record RefundRequest(long SaleId, IReadOnlyList<RefundLineRequest> Lines, int PaymentMethodId, string? Reason, bool Restock = true);

public sealed record CompletedRefund(
    long RefundId, string RefundNumber, decimal Total, string PaymentMethodName, PaymentKind Kind, string? CustomerName, decimal? CustomerBalance);

public interface IRefundService
{
    /// <summary>Finds a completed sale by its receipt number (as typed or scanned).</summary>
    Task<ReturnableSale> FindSaleAsync(string receiptNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the return and the money given back, in one transaction. Quantities are checked against what is
    /// still returnable while the original sale is locked, so two registers can't refund the same items.
    /// </summary>
    Task<CompletedRefund> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Refund slip contents for printing.</summary>
    Task<ReceiptData> GetRefundReceiptAsync(long refundId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refund amounts for returning <paramref name="quantity"/> of a line: the same share of what was charged for
    /// the line, so a discounted item gives back its discounted price. Returning everything that is left gives
    /// back exactly what remains of the original line, so rounding never over- or under-refunds.
    /// </summary>
    public static (decimal LineTotal, decimal Tax) AmountsFor(ReturnableLine line, int quantity)
    {
        if (quantity <= 0) return (0m, 0m);
        if (quantity == line.Remaining)
            return (line.LineTotal - line.RefundedTotal, line.TaxAmount - line.RefundedTax);
        var total = SaleCalculator.RoundMoney(line.LineTotal * quantity / line.Sold);
        return (total, SaleCalculator.LineTax(total, line.TaxRate));
    }
}

public class RefundService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<RefundService> logger) : IRefundService
{
    public const int MaxReasonLength = 200;

    public async Task<ReturnableSale> FindSaleAsync(string receiptNumber, CancellationToken cancellationToken = default)
    {
        Require();
        var number = receiptNumber.Trim();
        if (number.Length == 0) throw new PosException("Enter or scan the receipt number.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var saleId = await db.Sales.AsNoTracking()
            .Where(s => s.ReceiptNumber == number && s.Status == SaleStatus.Completed)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new PosException($"No sale with receipt number {number} was found.");
        return await LoadAsync(db, saleId, cancellationToken);
    }

    public async Task<CompletedRefund> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        var user = Require();
        var wanted = request.Lines.Where(l => l.Quantity > 0).ToList();
        if (wanted.Count == 0) throw new PosException("Choose at least one item to return.");
        if (wanted.GroupBy(l => l.SaleItemId).Any(g => g.Count() > 1)) throw new PosException("Each item may appear only once.");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > MaxReasonLength) throw new PosException($"Keep the reason to {MaxReasonLength} characters or fewer.");

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Serialise refunds of the same sale across registers.
            var sale = (await db.Sales.FromSqlInterpolated($"SELECT * FROM sales WHERE id = {request.SaleId} FOR UPDATE")
                    .ToListAsync(cancellationToken))
                .SingleOrDefault(s => s.Status == SaleStatus.Completed)
                ?? throw new PosException("That sale can no longer be refunded.");

            var original = await LoadAsync(db, sale.Id, cancellationToken);
            var method = await db.PaymentMethods.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.PaymentMethodId && m.IsActive, cancellationToken)
                ?? throw new PosException("That refund method is not available.");

            Customer? customer = null;
            if (method.Kind == PaymentKind.Account)
            {
                if (sale.CustomerId is not { } customerId)
                    throw new PosException("This sale has no customer, so it can't be refunded to an account.");
                customer = await CustomerLedger.LockAsync(db, customerId, cancellationToken);
            }
            var stocked = request.Restock
                ? await StockLedger.LockAsync(db, original.Lines.Where(l => wanted.Any(w => w.SaleItemId == l.SaleItemId)).Select(l => l.ProductId).ToList(), cancellationToken)
                : [];

            var refund = new Refund
            {
                RefundNumber = $"{sale.ReceiptNumber}-R{await db.Refunds.CountAsync(r => r.OriginalSaleId == sale.Id, cancellationToken) + 1}",
                OriginalSaleId = sale.Id,
                CashierId = user.Id,
                TerminalName = options.Value.TerminalName,
                PaymentMethodId = method.Id,
                Reason = reason,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };

            foreach (var want in wanted)
            {
                var line = original.Lines.FirstOrDefault(l => l.SaleItemId == want.SaleItemId)
                    ?? throw new PosException("One of the items is not part of this sale.");
                if (want.Quantity > line.Remaining)
                    throw new PosException(line.Remaining == 0
                        ? $"{line.Name} has already been fully returned."
                        : $"Only {line.Remaining} × {line.Name} can still be returned.");

                var (lineTotal, tax) = IRefundService.AmountsFor(line, want.Quantity);
                refund.Items.Add(new RefundItem
                {
                    SaleItemId = line.SaleItemId,
                    ProductId = line.ProductId,
                    ProductName = line.Name,
                    Quantity = want.Quantity,
                    UnitPrice = line.UnitPrice,
                    TaxRate = line.TaxRate,
                    TaxAmount = tax,
                    LineTotal = lineTotal,
                });
            }

            refund.Subtotal = refund.Items.Sum(i => i.LineTotal);
            refund.Tax = refund.Items.Sum(i => i.TaxAmount);
            refund.Total = refund.Subtotal + refund.Tax;
            db.Refunds.Add(refund);

            if (customer is not null)
            {
                var entry = CustomerLedger.Post(customer, CustomerTransactionType.Refund, -refund.Total, user.Id, options.Value.TerminalName, refund.CreatedAt);
                entry.Refund = refund;
                entry.Note = $"Return from receipt {sale.ReceiptNumber}";
                db.CustomerTransactions.Add(entry);
            }

            foreach (var product in stocked)
            {
                var back = refund.Items.Where(i => i.ProductId == product.Id).Sum(i => i.Quantity);
                var movement = StockLedger.Post(product, StockMovementType.Refund, back, user.Id, options.Value.TerminalName, refund.CreatedAt);
                movement.Refund = refund;
                movement.Note = $"Return from receipt {sale.ReceiptNumber}";
                db.StockMovements.Add(movement);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Refund {RefundNumber}: {Total} by {Method} for sale {SaleId}, {Lines} lines, user {UserId}, reason {Reason}, restocked {Restocked}",
                refund.RefundNumber, refund.Total, method.Code, sale.Id, refund.Items.Count, user.Id, reason, stocked.Count > 0);

            return new CompletedRefund(refund.Id, refund.RefundNumber, refund.Total, method.Name, method.Kind, customer?.Name, customer?.Balance);
        }
        catch (Exception ex) when (ex is not PosException and not OperationCanceledException)
        {
            logger.LogError(ex, "Refund for sale {SaleId} failed and was rolled back", request.SaleId);
            throw new PosException("Unable to complete the refund.\nPlease try again.", ex);
        }
    }

    public async Task<ReceiptData> GetRefundReceiptAsync(long refundId, CancellationToken cancellationToken = default)
    {
        Require();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var refund = await db.Refunds.AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Cashier)
            .Include(r => r.PaymentMethod)
            .Include(r => r.OriginalSale).ThenInclude(s => s!.Customer)
            .FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken)
            ?? throw new PosException("That refund could not be found.");
        var balance = await db.CustomerTransactions.AsNoTracking()
            .Where(t => t.RefundId == refundId).Select(t => (decimal?)t.BalanceAfter).FirstOrDefaultAsync(cancellationToken);

        return new ReceiptData(
            $"{refund.RefundNumber} (REFUND of {refund.OriginalSale!.ReceiptNumber})",
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(refund.CreatedAt, DateTimeKind.Utc), clock.LocalTimeZone),
            refund.Cashier?.DisplayName ?? "",
            refund.TerminalName,
            refund.OriginalSale.Customer?.Name,
            refund.OriginalSale.Customer?.AccountNumber,
            balance,
            refund.Items.OrderBy(i => i.Id).Select(i =>
            {
                // A discounted item comes back at its discounted price: show the full price and the discount kept back.
                var gross = SaleCalculator.LineTotal(i.UnitPrice, i.Quantity);
                return new ReceiptLine($"RETURN {i.ProductName}", i.Quantity, i.UnitPrice, -gross, Math.Max(0m, gross - i.LineTotal));
            }).ToList(),
            -refund.Subtotal,
            -refund.Tax,
            -refund.Total,
            [new ReceiptPayment($"Refund: {refund.PaymentMethod?.Name}", -refund.Total, null, null)]);
    }

    private static async Task<ReturnableSale> LoadAsync(PosDbContext db, long saleId, CancellationToken ct)
    {
        var sale = await db.Sales.AsNoTracking()
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstAsync(s => s.Id == saleId, ct);

        var returned = await db.RefundItems.AsNoTracking()
            .Where(r => r.Refund!.OriginalSaleId == saleId)
            .GroupBy(r => r.SaleItemId)
            .Select(g => new { SaleItemId = g.Key, Quantity = g.Sum(r => r.Quantity), Total = g.Sum(r => r.LineTotal), Tax = g.Sum(r => r.TaxAmount) })
            .ToDictionaryAsync(r => r.SaleItemId, ct);
        var alreadyRefunded = await db.Refunds.AsNoTracking().Where(r => r.OriginalSaleId == saleId).SumAsync(r => (decimal?)r.Total, ct) ?? 0m;

        return new ReturnableSale(
            sale.Id,
            sale.ReceiptNumber,
            sale.CreatedAt.ToLocalTime(),
            sale.Cashier?.DisplayName ?? "",
            sale.CustomerId,
            sale.Customer?.Name,
            sale.Total,
            alreadyRefunded,
            sale.Items.OrderBy(i => i.Id)
                .Select(i =>
                {
                    var done = returned.GetValueOrDefault(i.Id);
                    return new ReturnableLine(i.Id, i.ProductId, i.ProductName, i.Quantity, done?.Quantity ?? 0, i.UnitPrice, i.TaxRate,
                        i.LineTotal, i.TaxAmount, done?.Total ?? 0m, done?.Tax ?? 0m);
                })
                .ToList(),
            sale.Payments.OrderBy(p => p.Id)
                .Select(p => new OriginalPayment(p.PaymentMethodId, p.PaymentMethod?.Name ?? "", p.PaymentMethod?.Kind ?? PaymentKind.Other, p.Amount))
                .ToList());
    }

    private User Require()
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(Permission.ProcessReturns))
            throw new PosException("Returns need an administrator. Ask a manager to log in.");
        return user;
    }
}
