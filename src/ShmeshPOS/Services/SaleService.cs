using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <param name="Discount">Discount on this line, as entered; the amount is worked out by <see cref="SaleCalculator"/>.</param>
public sealed record SaleLineInput(int ProductId, string ProductName, decimal UnitPrice, decimal TaxRate, int Quantity, Discount? Discount = null);

/// <summary>One part of the payment.</summary>
/// <param name="Amount">Amount applied to the sale. Null means "whatever is still due".</param>
/// <param name="CashReceived">Cash handed over, for cash payments; anything above the amount is given back as change.</param>
public sealed record PaymentInput(int PaymentMethodId, decimal? Amount, decimal? CashReceived = null);

/// <param name="Payments">One or more payments that together cover the total, e.g. cash then card.</param>
public sealed record CompleteSaleRequest(
    IReadOnlyList<SaleLineInput> Lines, IReadOnlyList<PaymentInput> Payments, int? CustomerId = null, Discount? SaleDiscount = null)
{
    /// <summary>The whole sale paid one way.</summary>
    public CompleteSaleRequest(IReadOnlyList<SaleLineInput> lines, int paymentMethodId, decimal? cashReceived, int? customerId = null,
        Discount? saleDiscount = null)
        : this(lines, [new PaymentInput(paymentMethodId, null, cashReceived)], customerId, saleDiscount)
    {
    }
}

/// <param name="PaymentMethodName">How it was paid, e.g. "Cash" or "Cash + Credit Card".</param>
/// <param name="ChangeGiven">Change handed back; null when no cash was taken.</param>
/// <param name="CustomerBalance">The customer's new balance when (part of) the sale was charged to their account.</param>
public sealed record CompletedSale(
    long SaleId, string ReceiptNumber, decimal Total, string PaymentMethodName, decimal? ChangeGiven,
    string? CustomerName = null, decimal? CustomerBalance = null, decimal Discount = 0m)
{
    public bool TookCash => ChangeGiven is not null;
}

public interface ISaleService
{
    /// <summary>
    /// Records the sale, its lines, its payments and any stock and account changes in one database transaction.
    /// Either everything is saved or nothing is. Throws <see cref="PosException"/> with a cashier-safe message on failure.
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
    public const int MaxPayments = 10;

    public async Task<CompletedSale> CompleteSaleAsync(CompleteSaleRequest request, CancellationToken cancellationToken = default)
    {
        var cashier = session.CurrentUser ?? throw new PosException("Please log in to complete the sale.");
        if (!session.HasPermission(Permission.TakePayment))
            throw new PosException("You are not allowed to take payments.");

        Validate(request);
        var totals = SaleCalculator.Calculate(request.Lines, request.SaleDiscount);
        if (totals.Discount > 0 && !session.HasPermission(Permission.ApplyDiscounts))
            throw new PosException("Only an administrator can give discounts.\nRemove the discount or ask a manager.");

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var methodIds = request.Payments.Select(p => p.PaymentMethodId).Distinct().ToList();
            var methods = await db.PaymentMethods.AsNoTracking()
                .Where(m => methodIds.Contains(m.Id) && m.IsActive)
                .ToDictionaryAsync(m => m.Id, cancellationToken);
            if (methods.Count != methodIds.Count)
                throw new PosException("That payment method is not available.");

            var payments = ApplyPayments(request.Payments, methods, totals.Total);

            await EnsureProductsAvailableAsync(db, request.Lines, cancellationToken);

            // Charging an account locks the customer row until commit, so the limit check can't race another register.
            Customer? customer = null;
            var charged = payments.Where(p => p.Method.Kind == PaymentKind.Account).Sum(p => p.Amount);
            var chargesAccount = payments.Any(p => p.Method.Kind == PaymentKind.Account);
            if (chargesAccount)
            {
                if (!session.HasPermission(Permission.UseCustomerAccounts))
                    throw new PosException("You are not allowed to charge customer accounts.");
                var customerId = request.CustomerId ?? throw new PosException("Choose a customer before charging to an account.");
                customer = await CustomerLedger.LockAsync(db, customerId, cancellationToken);
                if (!customer.IsActive)
                    throw new PosException($"{customer.Name}'s account is closed.");

                var limit = customer.CreditLimit ?? await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken);
                if (customer.Balance + charged > limit)
                {
                    var available = Math.Max(0m, limit - customer.Balance);
                    throw new PosException(
                        $"{customer.Name} can't charge {charged:C}.\nThe account limit is {limit:C} and only {available:C} is available.");
                }
            }
            else if (request.CustomerId is { } attachedId)
            {
                customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == attachedId, cancellationToken)
                    ?? throw new PosException("That customer account no longer exists.");
            }

            // Locked after the customer (as refunds do) so the two never wait on each other in opposite order.
            var stocked = await StockLedger.LockAsync(db, request.Lines.Select(l => l.ProductId).ToList(), cancellationToken);

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
                Discount = totals.Discount,
                Status = SaleStatus.Completed,
                CreatedAt = now,
                Items = request.Lines.Select((l, i) =>
                {
                    var amounts = totals.Lines[i];
                    return new SaleItem
                    {
                        ProductId = l.ProductId,
                        ProductName = l.ProductName,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        TaxRate = l.TaxRate,
                        Discount = amounts.LineDiscount,
                        SaleDiscountShare = amounts.SaleDiscountShare,
                        TaxAmount = amounts.Tax,
                        LineTotal = amounts.Net,
                    };
                }).ToList(),
                Payments = payments.Select(p => new Payment
                {
                    PaymentMethodId = p.Method.Id,
                    Amount = p.Amount,
                    CashReceived = p.CashReceived,
                    ChangeGiven = p.Change,
                    CreatedAt = now,
                }).ToList(),
            };

            db.Sales.Add(sale);

            foreach (var product in stocked)
            {
                var sold = request.Lines.Where(l => l.ProductId == product.Id).Sum(l => l.Quantity);
                var movement = StockLedger.Post(product, StockMovementType.Sale, -sold, cashier.Id, options.Value.TerminalName, now);
                movement.Sale = sale;
                db.StockMovements.Add(movement);
            }

            if (chargesAccount)
            {
                var entry = CustomerLedger.Post(customer!, CustomerTransactionType.Charge, charged, cashier.Id, options.Value.TerminalName, now);
                entry.Sale = sale;
                db.CustomerTransactions.Add(entry);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var paidBy = string.Join(" + ", payments.Select(p => p.Method.Name).Distinct());
            logger.LogInformation(
                "Sale {ReceiptNumber} completed: {ItemCount} lines, total {Total}, discount {Discount}, paid by {Payments}, cashier {CashierId}, terminal {Terminal}",
                receiptNumber, sale.Items.Count, sale.Total, sale.Discount,
                string.Join(" + ", payments.Select(p => $"{p.Method.Code} {p.Amount}")), cashier.Id, sale.TerminalName);

            if (customer is not null)
                logger.LogInformation("Sale {ReceiptNumber} customer {CustomerId}; charged to account: {Charged}; balance {Balance}",
                    receiptNumber, customer.Id, charged, customer.Balance);

            foreach (var product in stocked.Where(p => p.IsLowOnStock))
                logger.LogInformation("Product {ProductId} ({Name}) is low on stock: {Quantity} left", product.Id, product.Name, product.StockQuantity);

            var cash = payments.Where(p => p.Method.Kind == PaymentKind.Cash).ToList();
            return new CompletedSale(sale.Id, receiptNumber, sale.Total, paidBy,
                cash.Count > 0 ? cash.Sum(p => p.Change ?? 0m) : null,
                customer?.Name, chargesAccount ? customer!.Balance : null, sale.Discount);
        }
        catch (Exception ex) when (ex is not PosException and not OperationCanceledException)
        {
            // The transaction was disposed without commit, so PostgreSQL has rolled everything back.
            logger.LogError(ex, "Sale failed and was rolled back (cashier {CashierId}, total {Total})", cashier.Id, totals.Total);
            throw new PosException("Unable to complete the sale.\nPlease try again.", ex);
        }
    }

    private sealed record AppliedPayment(PaymentMethod Method, decimal Amount, decimal? CashReceived, decimal? Change);

    /// <summary>
    /// Works out what each payment pays, in order. Together they must cover the total exactly; only cash can be
    /// over-tendered, and the extra is change.
    /// </summary>
    private static List<AppliedPayment> ApplyPayments(IReadOnlyList<PaymentInput> inputs, Dictionary<int, PaymentMethod> methods, decimal total)
    {
        var applied = new List<AppliedPayment>();
        var remaining = total;
        foreach (var input in inputs)
        {
            var method = methods[input.PaymentMethodId];
            if (remaining <= 0 && applied.Count > 0)
                throw new PosException("The sale is already paid in full. Remove the extra payment.");

            decimal amount;
            decimal? cashReceived = null, change = null;
            if (method.Kind == PaymentKind.Cash)
            {
                cashReceived = input.CashReceived ?? input.Amount ?? throw new PosException("Enter the cash received.");
                if (cashReceived < 0 || decimal.Round(cashReceived.Value, 2) != cashReceived)
                    throw new PosException("Enter the cash received as an amount, e.g. 20.00.");
                amount = input.Amount ?? Math.Min(cashReceived.Value, remaining);
                if (cashReceived < amount)
                    throw new PosException("Cash received is less than the amount due.");
                change = cashReceived - amount;
            }
            else
            {
                amount = input.Amount ?? remaining;
            }

            if (decimal.Round(amount, 2) != amount || amount < 0 || (amount == 0 && total > 0))
                throw new PosException("Each payment must be an amount above zero.");
            if (amount > remaining)
                throw new PosException($"{method.Name} {amount:C} is more than the {remaining:C} still due.");
            if (change > 0 && amount != remaining)
                throw new PosException("Change can only be given on the payment that settles the sale.");

            applied.Add(new AppliedPayment(method, amount, cashReceived, change));
            remaining -= amount;
        }

        if (remaining > 0)
            throw new PosException(applied.Count == 1 && applied[0].Method.Kind == PaymentKind.Cash
                ? "Cash received is less than the amount due."
                : $"The payments don't cover the total. {remaining:C} is still due.");
        if (applied.Count(p => p.Method.Kind == PaymentKind.Account) > 1)
            throw new PosException("Charge the account once per sale.");
        return applied;
    }

    private static void Validate(CompleteSaleRequest request)
    {
        var lines = request.Lines;
        if (lines.Count == 0)
            throw new PosException("The sale has no items.");
        if (lines.Any(l => l.Quantity is <= 0 or > MaxLineQuantity))
            throw new PosException($"Item quantities must be between 1 and {MaxLineQuantity}.");
        if (lines.Any(l => l.UnitPrice < 0 || l.TaxRate < 0))
            throw new PosException("The sale contains an invalid price.");
        if (lines.Select(l => l.Discount).Append(request.SaleDiscount).OfType<Discount>().FirstOrDefault(d => d.Problem is not null) is { } bad)
            throw new PosException(bad.Problem!);
        if (request.Payments.Count is 0 or > MaxPayments)
            throw new PosException($"A sale can be paid in 1 to {MaxPayments} parts.");
    }

    private static async Task EnsureProductsAvailableAsync(PosDbContext db, IReadOnlyList<SaleLineInput> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var found = await db.Products.Where(p => ids.Contains(p.Id) && p.IsActive).CountAsync(ct);
        if (found != ids.Count)
            throw new PosException("One or more items are no longer available.\nRemove them and try again.");
    }
}
