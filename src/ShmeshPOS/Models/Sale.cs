namespace ShmeshPOS.Models;

public class Sale
{
    public long Id { get; set; }

    /// <summary>Human-facing receipt number, e.g. 2026-000001. Independent of <see cref="Id"/>.</summary>
    public required string ReceiptNumber { get; set; }

    public int CashierId { get; set; }
    public User? Cashier { get; set; }

    /// <summary>Customer the sale was made to, if one was attached. Required when paid by account.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Name of the POS terminal that recorded the sale.</summary>
    public required string TerminalName { get; set; }

    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public DateTime CreatedAt { get; set; }

    public List<SaleItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}
