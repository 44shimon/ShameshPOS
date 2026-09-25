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

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
