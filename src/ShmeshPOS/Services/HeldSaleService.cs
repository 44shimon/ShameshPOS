using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public sealed record HeldSaleSummary(
    int Id, string? Label, DateTime Time, string Cashier, string Terminal, string? CustomerName, int ItemCount, decimal Total);

/// <summary>What comes back into the cart when a held sale is retrieved.</summary>
public sealed record HeldSaleContent(int Id, string? Label, int? CustomerId, IReadOnlyList<SaleLineInput> Lines, Discount? SaleDiscount = null);

public interface IHeldSaleService
{
    /// <summary>Parks the cart, discounts included. Returns the held sale's id.</summary>
    Task<int> HoldAsync(IReadOnlyList<SaleLineInput> lines, int? customerId, string? label, Discount? saleDiscount = null,
        CancellationToken cancellationToken = default);

    /// <summary>All held sales on every register, oldest first.</summary>
    Task<IReadOnlyList<HeldSaleSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a held sale back (and removes it), so two registers can't retrieve the same one.</summary>
    Task<HeldSaleContent> RetrieveAsync(int heldSaleId, CancellationToken cancellationToken = default);

    Task DeleteAsync(int heldSaleId, CancellationToken cancellationToken = default);
}

public class HeldSaleService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<HeldSaleService> logger) : IHeldSaleService
{
    public async Task<int> HoldAsync(IReadOnlyList<SaleLineInput> lines, int? customerId, string? label, Discount? saleDiscount = null,
        CancellationToken cancellationToken = default)
    {
        var user = Require();
        if (lines.Count == 0) throw new PosException("There is nothing to hold.");
        var cleanLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (cleanLabel?.Length > 60) throw new PosException("Keep the note to 60 characters or fewer.");

        var totals = SaleCalculator.Calculate(lines, saleDiscount);
        var held = new HeldSale
        {
            Label = cleanLabel,
            CashierId = user.Id,
            CustomerId = customerId,
            TerminalName = options.Value.TerminalName,
            Total = totals.Total,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            SaleDiscountKind = saleDiscount?.Kind,
            SaleDiscountValue = saleDiscount?.Value,
            Items = lines.Select((l, i) => new HeldSaleItem
            {
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                TaxRate = l.TaxRate,
                Position = i,
                DiscountKind = l.Discount?.Kind,
                DiscountValue = l.Discount?.Value,
            }).ToList(),
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.HeldSales.Add(held);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sale held as {HeldSaleId} ({Label}): {Lines} lines, total {Total}, user {UserId}",
            held.Id, cleanLabel, lines.Count, held.Total, user.Id);
        return held.Id;
    }

    public async Task<IReadOnlyList<HeldSaleSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        Require();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.HeldSales.AsNoTracking()
            .OrderBy(h => h.CreatedAt)
            .Select(h => new
            {
                h.Id, h.Label, h.CreatedAt, Cashier = h.Cashier!.DisplayName, h.TerminalName,
                Customer = h.Customer!.Name, Items = h.Items.Sum(i => i.Quantity), h.Total,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new HeldSaleSummary(r.Id, r.Label, ToLocal(r.CreatedAt), r.Cashier, r.TerminalName, r.Customer, r.Items, r.Total))
            .ToList();
    }

    public async Task<HeldSaleContent> RetrieveAsync(int heldSaleId, CancellationToken cancellationToken = default)
    {
        var user = Require();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock the row: if another register is retrieving it right now, this waits and then finds it gone.
        var held = (await db.HeldSales.FromSqlInterpolated($"SELECT * FROM held_sales WHERE id = {heldSaleId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault()
            ?? throw new PosException("That held sale was already retrieved on another register.");
        var items = await db.HeldSaleItems.Where(i => i.HeldSaleId == held.Id).OrderBy(i => i.Position).ToListAsync(cancellationToken);

        db.HeldSales.Remove(held);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Held sale {HeldSaleId} retrieved by user {UserId} on {Terminal}", held.Id, user.Id, options.Value.TerminalName);
        return new HeldSaleContent(held.Id, held.Label, held.CustomerId,
            items.Select(i => new SaleLineInput(i.ProductId, i.ProductName, i.UnitPrice, i.TaxRate, i.Quantity, ToDiscount(i.DiscountKind, i.DiscountValue))).ToList(),
            ToDiscount(held.SaleDiscountKind, held.SaleDiscountValue));
    }

    public async Task DeleteAsync(int heldSaleId, CancellationToken cancellationToken = default)
    {
        var user = Require();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.HeldSales.Where(h => h.Id == heldSaleId).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
            logger.LogInformation("Held sale {HeldSaleId} deleted by user {UserId}", heldSaleId, user.Id);
    }

    private User Require()
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(Permission.HoldSales))
            throw new PosException("You are not allowed to hold sales.");
        return user;
    }

    private static Discount? ToDiscount(DiscountKind? kind, decimal? value) =>
        kind is { } k && value is { } v ? new Discount(k, v) : null;

    private DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone);
}
