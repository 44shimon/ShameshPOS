namespace ShmeshPOS.Models;

/// <summary>Per-year receipt counter. Incremented inside the sale transaction, so numbers are unique and gap-free.</summary>
public class ReceiptSequence
{
    public int Year { get; set; }
    public int LastNumber { get; set; }
}
