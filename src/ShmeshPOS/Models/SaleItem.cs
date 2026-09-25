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

    /// <summary>Quantity × unit price, tax exclusive.</summary>
    public decimal LineTotal { get; set; }
}
