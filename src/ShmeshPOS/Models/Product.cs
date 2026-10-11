namespace ShmeshPOS.Models;

public class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Short cashier-facing product code (e.g. "101") for quick keyed entry.</summary>
    public string? Code { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Unit price, tax exclusive.</summary>
    public decimal Price { get; set; }

    /// <summary>Tax rate as a fraction, e.g. 0.0875 for 8.75%.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>Button order on the register's main screen (and in lists).</summary>
    public int SortOrder { get; set; }

    /// <summary>Shown as a button on the register's main screen. At most <see cref="Services.CatalogAdminService.MaxMainScreenItems"/> products.</summary>
    public bool ShowOnMainScreen { get; set; }

    /// <summary>Count this product's stock: sales take it out, deliveries and returns put it back.</summary>
    public bool TrackStock { get; set; }

    /// <summary>Quantity on hand when <see cref="TrackStock"/> is on. Can go below zero if more is sold than was counted.</summary>
    public int StockQuantity { get; set; }

    /// <summary>Show the product as low on stock at or below this quantity. Null: no alert.</summary>
    public int? ReorderLevel { get; set; }

    public bool IsLowOnStock => TrackStock && StockQuantity <= (ReorderLevel ?? 0);
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
