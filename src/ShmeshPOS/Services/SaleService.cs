using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public sealed record SaleLineInput(int ProductId, string ProductName, decimal UnitPrice, decimal TaxRate, int Quantity);

public sealed record CompleteSaleRequest(IReadOnlyList<SaleLineInput> Lines, int PaymentMethodId, decimal? CashReceived, int? CustomerId = null);

/// <param name="CustomerBalance">The customer's new balance when the sale was charged to their account.</param>
public sealed record CompletedSale(
    long SaleId, string ReceiptNumber, decimal Total, string PaymentMethodName, decimal? ChangeGiven,
    string? CustomerName = null, decimal? CustomerBalance = null);

public interface ISaleService
{
    /// <summary>
    /// Records the sale, its lines and its payment in one database transaction. Either everything is
    /// saved or nothing is. Throws <see cref="PosException"/> with a cashier-safe message on failure.
    /// </summary>
    Task<CompletedSale> CompleteSaleAsync(CompleteSaleRequest request, CancellationToken cancellationToken = default);
}

public class SaleService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IReceiptNumberGenerator receiptNumbers,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<SaleService> logger) : ISaleService
{
    public const int MaxLineQuantity = 999;

    public async Task<CompletedSale> CompleteSaleAsync(CompleteSaleRequest request, CancellationToken cancellationToken = default)
    {
        var cashier = session.CurrentUser ?? throw new PosException("Please log in to complete the sale.");
        if (!session.HasPermission(Permission.TakePayment))
            throw new PosException("You are not allowed to take payments.");

        Validate(request.Lines);
        var totals = SaleCalculator.Calculate(request.Lines.Select(l => (l.UnitPrice, l.Quantity, l.TaxRate)));

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var method = await db.PaymentMethods.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.PaymentMethodId && m.IsActive, cancellationToken)
                ?? throw new PosException("That payment method is not available.");

            await EnsureProductsAvailableAsync(db, request.Lines, cancellationToken);

            // Charging an account locks the customer row until commit, so the limit check can't race another register.
            Customer? customer = null;
            if (method.Kind == PaymentKind.Account)
            {
                if (!session.HasPermission(Permission.UseCustomerAccounts))
                    throw new PosException("You are not allowed to charge customer accounts.");
                var customerId = request.CustomerId ?? throw new PosException("Choose a customer before charging to an account.");
                customer = await CustomerLedger.LockAsync(db, customerId, cancellationToken);
                if (!customer.IsActive)
                    throw new PosException($"{customer.Name}'s account is closed.");

                var limit = customer.CreditLimit ?? await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken);
                if (customer.Balance + totals.Total > limit)
                {
                    var available = Math.Max(0m, limit - customer.Balance);
                    throw new PosException(
                        $"{customer.Name} can't charge {totals.Total:C}.\nThe account limit is {limit:C} and only {available:C} is available.");
                }
            }
            else if (request.CustomerId is { } attachedId)
            {
                customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == attachedId, cancellationToken)
                    ?? throw new PosException("That customer account no longer exists.");
            }

            decimal? cashReceived = null, change = null;
            if (method.Kind == PaymentKind.Cash)
            {
                cashReceived = request.CashReceived ?? throw new PosException("Enter the cash received.");
                change = SaleCalculator.CalculateChange(totals.Total, cashReceived.Value)
                    ?? throw new PosException("Cash received is less than the amount due.");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var receiptNumber = await receiptNumbers.NextAsync(db, clock.GetLocalNow().Year, cancellationToken);

            var sale = new Sale
            {
                ReceiptNumber = receiptNumber,
                CashierId = cashier.Id,
                CustomerId = customer?.Id,
                TerminalName = options.Value.TerminalName,
                Subtotal = totals.Subtotal,
                Tax = totals.Tax,
                Total = totals.Total,
                Status = SaleStatus.Completed,
                CreatedAt = now,
                Items = request.Lines.Select(l =>
                {
                    var lineTotal = SaleCalculator.LineTotal(l.UnitPrice, l.Quantity);
                    return new SaleItem
                    {
                        ProductId = l.ProductId,
                        ProductName = l.ProductName,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        TaxRate = l.TaxRate,
                        TaxAmount = SaleCalculator.LineTax(lineTotal, l.TaxRate),
                        LineTotal = lineTotal,
                    };
                }).ToList(),
                Payments =
                [
                    new Payment
                    {
                        PaymentMethodId = method.Id,
                        Amount = totals.Total,
                        CashReceived = cashReceived,
                        ChangeGiven = change,
                        CreatedAt = now,
                    },
                ],
            };

            db.Sales.Add(sale);

            if (method.Kind == PaymentKind.Account)
            {
                var entry = CustomerLedger.Post(customer!, CustomerTransactionType.Charge, totals.Total, cashier.Id, options.Value.TerminalName, now);
                entry.Sale = sale;
                db.CustomerTransactions.Add(entry);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Sale {ReceiptNumber} completed: {ItemCount} lines, total {Total}, paid by {PaymentMethod}, cashier {CashierId}, terminal {Terminal}",
                receiptNumber, sale.Items.Count, sale.Total, method.Code, cashier.Id, sale.TerminalName);

            if (customer is not null)
                logger.LogInformation("Sale {ReceiptNumber} customer {CustomerId}; charged to account: {Charged}; balance {Balance}",
                    receiptNumber, customer.Id, method.Kind == PaymentKind.Account, customer.Balance);

            return new CompletedSale(sale.Id, receiptNumber, sale.Total, method.Name, change,
                customer?.Name, method.Kind == PaymentKind.Account ? customer!.Balance : null);
        }
        catch (Exception ex) when (ex is not PosException and not OperationCanceledException)
        {
            // The transaction was disposed without commit, so PostgreSQL has rolled everything back.
            logger.LogError(ex, "Sale failed and was rolled back (cashier {CashierId}, total {Total})", cashier.Id, totals.Total);
            throw new PosException("Unable to complete the sale.\nPlease try again.", ex);
        }
    }

    private static void Validate(IReadOnlyList<SaleLineInput> lines)
    {
        if (lines.Count == 0)
            throw new PosException("The sale has no items.");
        if (lines.Any(l => l.Quantity is <= 0 or > MaxLineQuantity))
            throw new PosException($"Item quantities must be between 1 and {MaxLineQuantity}.");
        if (lines.Any(l => l.UnitPrice < 0 || l.TaxRate < 0))
            throw new PosException("The sale contains an invalid price.");
    }

    private static async Task EnsureProductsAvailableAsync(PosDbContext db, IReadOnlyList<SaleLineInput> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var found = await db.Products.Where(p => ids.Contains(p.Id) && p.IsActive).CountAsync(ct);
        if (found != ids.Count)
            throw new PosException("One or more items are no longer available.\nRemove them and try again.");
    }
}
