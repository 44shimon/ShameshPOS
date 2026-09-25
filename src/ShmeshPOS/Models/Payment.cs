namespace ShmeshPOS.Models;

public class Payment
{
    public long Id { get; set; }

    public long SaleId { get; set; }
    public Sale? Sale { get; set; }

    public int PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    /// <summary>Amount applied to the sale.</summary>
    public decimal Amount { get; set; }

    /// <summary>Cash handed over by the customer. Only set for cash payments.</summary>
    public decimal? CashReceived { get; set; }

    /// <summary>Change returned to the customer. Only set for cash payments.</summary>
    public decimal? ChangeGiven { get; set; }

    public DateTime CreatedAt { get; set; }
}
