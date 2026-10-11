using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <param name="TrackStock">Count stock for this product. The quantity itself changes only through sales, returns, deliveries and counts.</param>
/// <param name="ReorderLevel">Show as low on stock at or below this quantity.</param>
public sealed record ProductInput(
    int Id, string Name, int CategoryId, string? Code, string? Sku, string? Barcode,
    decimal Price, decimal TaxRate, int SortOrder, bool IsActive, bool ShowOnMainScreen = false,
    bool TrackStock = false, int? ReorderLevel = null);

public sealed record CategoryInput(int Id, string Name, string? Color, int SortOrder, bool IsActive);

public interface ICatalogAdminService
{
    /// <summary>All categories, including disabled ones.</summary>
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>All products with their category, including disabled ones.</summary>
    Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the product when <see cref="ProductInput.Id"/> is 0, otherwise updates it.</summary>
    Task<Product> SaveProductAsync(ProductInput input, CancellationToken cancellationToken = default);

    /// <summary>Creates the category when <see cref="CategoryInput.Id"/> is 0, otherwise updates it.</summary>
    Task<Category> SaveCategoryAsync(CategoryInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Product and category maintenance. Nothing is ever deleted: sales reference products, so they are disabled
/// instead. Every change is permission-checked here (not just hidden in the UI) and written to the log.
/// </summary>
public class CatalogAdminService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    TimeProvider clock,
    ILogger<CatalogAdminService> logger) : ICatalogAdminService
{
    public const decimal MaxPrice = 99_999_999.99m;

    /// <summary>How many product buttons fit on the register's main screen.</summary>
    public const int MaxMainScreenItems = 10;

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Categories.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Products.AsNoTracking()
            .Include(p => p.Category)
            .OrderBy(p => p.Category!.SortOrder).ThenBy(p => p.SortOrder).ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Product> SaveProductAsync(ProductInput input, CancellationToken cancellationToken = default)
    {
        var admin = RequirePermission();
        var name = Clean(input.Name) ?? throw new PosException("Enter a product name.");
        if (name.Length > 100) throw new PosException("Product name must be 100 characters or fewer.");
        if (input.Price is < 0 or > MaxPrice) throw new PosException("Enter a price between $0.00 and $99,999,999.99.");
        if (decimal.Round(input.Price, 2) != input.Price) throw new PosException("Price can have at most 2 decimal places.");
        if (input.TaxRate is < 0 or >= 1) throw new PosException("Tax rate must be between 0% and 99.999%.");
        if (decimal.Round(input.TaxRate, 5) != input.TaxRate) throw new PosException("Tax rate can have at most 3 decimal places.");
        if (input.ReorderLevel is < 0 or > StockService.MaxQuantity) throw new PosException("The low-stock level must be a whole number, 0 or more.");

        var code = CleanKey(input.Code, 20, "Product code");
        var sku = CleanKey(input.Sku, 50, "SKU");
        var barcode = CleanKey(input.Barcode, 50, "Barcode");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Categories.AnyAsync(c => c.Id == input.CategoryId, cancellationToken))
            throw new PosException("Choose a category.");

        // Code, SKU and barcode share one lookup (search box / scanner), so a value may belong to only one product.
        foreach (var key in new[] { code, sku, barcode }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var lower = key.ToLowerInvariant();
            var clash = await db.Products.AsNoTracking()
                .Where(p => p.Id != input.Id &&
                            (p.Code!.ToLower() == lower || p.Sku!.ToLower() == lower || p.Barcode!.ToLower() == lower))
                .Select(p => p.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (clash is not null)
                throw new PosException($"“{key}” is already used by {clash}.");
        }

        if (input.ShowOnMainScreen)
        {
            var others = await db.Products.CountAsync(p => p.Id != input.Id && p.ShowOnMainScreen, cancellationToken);
            if (others >= MaxMainScreenItems)
                throw new PosException($"The main screen already has {MaxMainScreenItems} items. Untick “Show on main screen” on another product first.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        Product product;
        string? changes = null;
        if (input.Id == 0)
        {
            product = new Product { Name = name, CreatedAt = now };
            db.Products.Add(product);
        }
        else
        {
            product = await db.Products.FirstOrDefaultAsync(p => p.Id == input.Id, cancellationToken)
                ?? throw new PosException("That product no longer exists.");
        }

        var before = input.Id == 0 ? null : Describe(product);
        product.Name = name;
        product.CategoryId = input.CategoryId;
        product.Code = code;
        product.Sku = sku;
        product.Barcode = barcode;
        product.Price = input.Price;
        product.TaxRate = input.TaxRate;
        product.SortOrder = input.SortOrder;
        product.IsActive = input.IsActive;
        product.ShowOnMainScreen = input.ShowOnMainScreen;
        product.TrackStock = input.TrackStock;
        product.ReorderLevel = input.ReorderLevel;
        product.UpdatedAt = now;
        if (before is not null) changes = DescribeChanges(before, Describe(product));

        await SaveAsync(db, "product", cancellationToken);

        if (input.Id == 0)
            logger.LogInformation("Admin {AdminId} created product {ProductId} {@Product}", admin.Id, product.Id, Describe(product));
        else if (changes is not null)
            logger.LogInformation("Admin {AdminId} updated product {ProductId}: {Changes}", admin.Id, product.Id, changes);

        return product;
    }

    public async Task<Category> SaveCategoryAsync(CategoryInput input, CancellationToken cancellationToken = default)
    {
        var admin = RequirePermission();
        var name = Clean(input.Name) ?? throw new PosException("Enter a category name.");
        if (name.Length > 60) throw new PosException("Category name must be 60 characters or fewer.");
        var color = Clean(input.Color);
        if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            throw new PosException("Color must be a hex value such as #2563EB.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var lower = name.ToLowerInvariant();
        if (await db.Categories.AnyAsync(c => c.Id != input.Id && c.Name.ToLower() == lower, cancellationToken))
            throw new PosException($"A category named “{name}” already exists.");

        Category category;
        if (input.Id == 0)
        {
            category = new Category { Name = name };
            db.Categories.Add(category);
        }
        else
        {
            category = await db.Categories.FirstOrDefaultAsync(c => c.Id == input.Id, cancellationToken)
                ?? throw new PosException("That category no longer exists.");
        }

        var before = input.Id == 0 ? null : $"name={category.Name}, color={category.Color}, sort={category.SortOrder}, active={category.IsActive}";
        category.Name = name;
        category.Color = color;
        category.SortOrder = input.SortOrder;
        category.IsActive = input.IsActive;

        await SaveAsync(db, "category", cancellationToken);

        logger.LogInformation(input.Id == 0
                ? "Admin {AdminId} created category {CategoryId} ({Before} → {After})"
                : "Admin {AdminId} updated category {CategoryId} ({Before} → {After})",
            admin.Id, category.Id, before ?? "new",
            $"name={category.Name}, color={category.Color}, sort={category.SortOrder}, active={category.IsActive}");
        return category;
    }

    private User RequirePermission()
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(Permission.ManageProducts))
            throw new PosException("You are not allowed to change products.");
        return user;
    }

    private async Task SaveAsync(PosDbContext db, string what, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another register saved a clashing value between our check and our write.
            throw new PosException($"This {what} clashes with one that was just saved elsewhere. Reload and try again.", ex);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CleanKey(string? value, int maxLength, string label)
    {
        var key = Clean(value);
        if (key is not null && key.Length > maxLength)
            throw new PosException($"{label} must be {maxLength} characters or fewer.");
        return key;
    }

    private static Dictionary<string, object?> Describe(Product p) => new()
    {
        ["Name"] = p.Name,
        ["CategoryId"] = p.CategoryId,
        ["Code"] = p.Code,
        ["Sku"] = p.Sku,
        ["Barcode"] = p.Barcode,
        ["Price"] = p.Price,
        ["TaxRate"] = p.TaxRate,
        ["SortOrder"] = p.SortOrder,
        ["IsActive"] = p.IsActive,
        ["ShowOnMainScreen"] = p.ShowOnMainScreen,
        ["TrackStock"] = p.TrackStock,
        ["ReorderLevel"] = p.ReorderLevel,
    };

    private static string? DescribeChanges(Dictionary<string, object?> before, Dictionary<string, object?> after)
    {
        var changes = after
            .Where(kv => !Equals(before[kv.Key], kv.Value))
            .Select(kv => $"{kv.Key} {before[kv.Key] ?? "∅"} → {kv.Value ?? "∅"}")
            .ToList();
        return changes.Count == 0 ? null : string.Join("; ", changes);
    }
}
