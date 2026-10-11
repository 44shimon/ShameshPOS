using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public interface IStockService
{
    /// <summary>Adds a delivery to the quantity on hand. Returns the new quantity.</summary>
    Task<int> ReceiveAsync(int productId, int quantity, string? note, CancellationToken cancellationToken = default);

    /// <summary>Sets the quantity on hand to what was counted; the difference is recorded. Returns the new quantity.</summary>
    Task<int> CountAsync(int productId, int countedQuantity, string? note, CancellationToken cancellationToken = default);

    /// <summary>Most recent stock changes for a product, newest first.</summary>
    Task<IReadOnlyList<StockMovement>> GetMovementsAsync(int productId, int take = 30, CancellationToken cancellationToken = default);
}

/// <summary>Deliveries and stock counts from Administration → Products. Sales and returns move stock in their own transactions.</summary>
public class StockService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<StockService> logger) : IStockService
{
    public const int MaxQuantity = 1_000_000;

    public Task<int> ReceiveAsync(int productId, int quantity, string? note, CancellationToken cancellationToken = default)
    {
        if (quantity is <= 0 or > MaxQuantity)
            throw new PosException("Enter how many were received (a whole number above zero).");
        return PostAsync(productId, StockMovementType.Received, _ => quantity, note, cancellationToken);
    }

    public Task<int> CountAsync(int productId, int countedQuantity, string? note, CancellationToken cancellationToken = default)
    {
        if (countedQuantity is < 0 or > MaxQuantity)
            throw new PosException("Enter the quantity counted (a whole number, 0 or more).");
        return PostAsync(productId, StockMovementType.Count, current => countedQuantity - current, note, cancellationToken);
    }

    public async Task<IReadOnlyList<StockMovement>> GetMovementsAsync(int productId, int take = 30, CancellationToken cancellationToken = default)
    {
        Require();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.StockMovements.AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Sale)
            .Where(m => m.ProductId == productId)
            .OrderByDescending(m => m.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> PostAsync(int productId, StockMovementType type, Func<int, int> change, string? note, CancellationToken ct)
    {
        var user = Require();
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote?.Length > 200) throw new PosException("Keep the note to 200 characters or fewer.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var product = (await StockLedger.LockAsync(db, [productId], ct)).SingleOrDefault()
            ?? throw new PosException("Turn on “Track stock” for this product first.");
        var before = product.StockQuantity;
        var movement = StockLedger.Post(product, type, change(before), user.Id, options.Value.TerminalName, clock.GetUtcNow().UtcDateTime);
        if (Math.Abs(product.StockQuantity) > MaxQuantity)
            throw new PosException("That would take the stock above the largest quantity allowed.");
        movement.Note = cleanNote;
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation("User {UserId} recorded stock {Type} for product {ProductId}: {Before} → {After} ({Note})",
            user.Id, type, product.Id, before, product.StockQuantity, cleanNote);
        return product.StockQuantity;
    }

    private User Require()
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(Permission.ManageStock))
            throw new PosException("You are not allowed to change stock.");
        return user;
    }
}

/// <summary>Stock changes shared by sales, returns, deliveries and counts.</summary>
public static class StockLedger
{
    /// <summary>
    /// Loads the stock-tracked products among <paramref name="productIds"/> with a row lock held until the
    /// surrounding transaction ends, so two registers selling the last item record the stock correctly.
    /// Rows are locked in id order so concurrent sales of the same items can't deadlock.
    /// </summary>
    public static async Task<List<Product>> LockAsync(PosDbContext db, IReadOnlyCollection<int> productIds, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Product rows must be locked inside a transaction.");
        if (productIds.Count == 0) return [];

        var ids = productIds.Distinct().ToArray();
        return await db.Products
            .FromSqlInterpolated($"SELECT * FROM products WHERE id = ANY({ids}) AND track_stock ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
    }

    /// <summary>Applies <paramref name="change"/> to the quantity on hand and returns the matching history entry (not yet added to the context).</summary>
    public static StockMovement Post(Product product, StockMovementType type, int change, int userId, string terminal, DateTime now)
    {
        product.StockQuantity += change;
        return new StockMovement
        {
            ProductId = product.Id,
            Type = type,
            Change = change,
            QuantityAfter = product.StockQuantity,
            UserId = userId,
            TerminalName = terminal,
            CreatedAt = now,
        };
    }
}
