namespace ShmeshPOS.Models;

/// <summary>
/// A sale parked with "Hold Sale" so the register can serve someone else. Held sales are shared by all
/// registers; retrieving one moves its items back into a cart and deletes the held record.
/// </summary>
public class HeldSale
{
    public int Id { get; set; }

    /// <summary>Optional note to recognise it, e.g. the customer's name or "table 4".</summary>
    public string? Label { get; set; }

    public int CashierId { get; set; }
    public User? Cashier { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public required string TerminalName { get; set; }
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Discount on the whole sale, as the cashier entered it.</summary>
    public DiscountKind? SaleDiscountKind { get; set; }
    public decimal? SaleDiscountValue { get; set; }

    public List<HeldSaleItem> Items { get; set; } = [];
}

/// <summary>A line of a held sale, with the price the customer was quoted.</summary>
public class HeldSaleItem
{
    public int Id { get; set; }

    public int HeldSaleId { get; set; }
    public HeldSale? HeldSale { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public int Position { get; set; }

    /// <summary>Discount on this line, as the cashier entered it.</summary>
    public DiscountKind? DiscountKind { get; set; }
    public decimal? DiscountValue { get; set; }
}
