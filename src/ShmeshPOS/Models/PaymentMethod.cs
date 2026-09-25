namespace ShmeshPOS.Models;

public class PaymentMethod
{
    public int Id { get; set; }

    /// <summary>Stable identifier such as CASH or CREDIT. Display names can change; codes should not.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public PaymentKind Kind { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
