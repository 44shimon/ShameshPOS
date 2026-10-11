namespace ShmeshPOS.Models;

public enum StockMovementType
{
    /// <summary>Sold at the register.</summary>
    Sale,

    /// <summary>Returned by a customer and put back on the shelf.</summary>
    Refund,

    /// <summary>A delivery was received.</summary>
    Received,

    /// <summary>A stock count set the quantity to what is really on hand.</summary>
    Count,
}

/// <summary>
/// One change to a product's stock. The product's quantity on hand is always the sum of these changes;
/// each row also records the quantity it left behind, so the history reads like a stock card.
/// </summary>
public class StockMovement
{
    public long Id { get; set; }

    public int ProductId { get; set; }
    public Product? Product { get; set; }

    public StockMovementType Type { get; set; }

    /// <summary>Change to the quantity on hand: negative for a sale, positive for a delivery or return.</summary>
    public int Change { get; set; }

    public int QuantityAfter { get; set; }

    public long? SaleId { get; set; }
    public Sale? Sale { get; set; }

    public long? RefundId { get; set; }
    public Refund? Refund { get; set; }

    public string? Note { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public string? TerminalName { get; set; }
    public DateTime CreatedAt { get; set; }
}
