namespace ShmeshPOS.Models;

/// <summary>
/// Money given back for items returned from an earlier sale. The original sale is never changed; returned
/// quantities are the sum of its refund lines, which can never exceed what was sold.
/// </summary>
public class Refund
{
    public long Id { get; set; }

    /// <summary>The original receipt number with a suffix, e.g. 2026-000005-R1.</summary>
    public required string RefundNumber { get; set; }

    public long OriginalSaleId { get; set; }
    public Sale? OriginalSale { get; set; }

    public int CashierId { get; set; }
    public User? Cashier { get; set; }

    public required string TerminalName { get; set; }

    /// <summary>How the money went back: cash from the drawer, card, or credited to the customer's account.</summary>
    public int PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    /// <summary>Amounts refunded, as positive numbers.</summary>
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }

    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<RefundItem> Items { get; set; } = [];
}

public class RefundItem
{
    public long Id { get; set; }

    public long RefundId { get; set; }
    public Refund? Refund { get; set; }

    /// <summary>The line of the original sale being returned.</summary>
    public long SaleItemId { get; set; }
    public SaleItem? SaleItem { get; set; }

    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }
}
