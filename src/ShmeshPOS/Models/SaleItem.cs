namespace ShmeshPOS.Models;

/// <summary>
/// A line on a sale. Name, price and tax rate are copied from the product at the time of sale
/// so history stays correct after the product is renamed or repriced.
/// </summary>
public class SaleItem
{
    public long Id { get; set; }

    public long SaleId { get; set; }
    public Sale? Sale { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }

    /// <summary>Discount given on this line itself, before tax.</summary>
    public decimal Discount { get; set; }

    /// <summary>This line's share of a discount on the whole sale, before tax.</summary>
    public decimal SaleDiscountShare { get; set; }

    /// <summary>Quantity × unit price less <see cref="Discount"/> and <see cref="SaleDiscountShare"/>, tax exclusive. Tax is charged on this.</summary>
    public decimal LineTotal { get; set; }
}
