using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Data;
using ShmeshPOS.Models;

namespace ShmeshPOS.Services;

public interface IProductService
{
    IReadOnlyList<Category> Categories { get; }
    IReadOnlyList<Product> Products { get; }

    /// <summary>Loads active categories and products into memory. Searches never hit the database.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Filters the in-memory catalog by name, code, SKU or barcode, best matches first.</summary>
    IReadOnlyList<Product> Search(string? text, int? categoryId = null);

    /// <summary>Exact code/SKU/barcode match, as typed by a cashier or sent by a barcode scanner.</summary>
    Product? FindByLookupKey(string text);
}

public class ProductService(IDbContextFactory<PosDbContext> dbFactory, ILogger<ProductService> logger) : IProductService
{
    private Dictionary<string, Product> _byLookupKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Category> Categories { get; private set; } = [];
    public IReadOnlyList<Product> Products { get; private set; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var products = await db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive && p.Category!.IsActive)
            .OrderBy(p => p.Category!.SortOrder).ThenBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        var byKey = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
        {
            foreach (var key in new[] { p.Barcode, p.Sku, p.Code })
            {
                if (!string.IsNullOrWhiteSpace(key))
                    byKey.TryAdd(key.Trim(), p);
            }
        }

        Categories = categories;
        Products = products;
        _byLookupKey = byKey;
        logger.LogInformation("Catalog loaded: {CategoryCount} categories, {ProductCount} products", categories.Count, products.Count);
    }

    public Product? FindByLookupKey(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : _byLookupKey.GetValueOrDefault(text.Trim());

    public IReadOnlyList<Product> Search(string? text, int? categoryId = null)
    {
        var term = text?.Trim() ?? "";
        if (term.Length == 0)
            return categoryId is null ? Products : Products.Where(p => p.CategoryId == categoryId).ToList();

        // Rank: exact lookup key, then name prefix, then key prefix, then name contains.
        return Products
            .Select(p => (Product: p, Rank: Rank(p, term)))
            .Where(x => x.Rank < int.MaxValue)
            .OrderBy(x => x.Rank)
            .Select(x => x.Product)
            .ToList();
    }

    private static int Rank(Product p, string term)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        string?[] keys = [p.Barcode, p.Sku, p.Code];

        if (keys.Any(k => string.Equals(k, term, ic))) return 0;
        if (p.Name.StartsWith(term, ic)) return 1;
        if (keys.Any(k => k?.StartsWith(term, ic) == true)) return 2;
        if (p.Name.Contains(term, ic)) return 3;
        return int.MaxValue;
    }
}
