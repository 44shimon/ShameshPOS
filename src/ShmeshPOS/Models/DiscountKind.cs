namespace ShmeshPOS.Models;

/// <summary>How a discount is given: a percentage of the price, or a fixed amount off.</summary>
public enum DiscountKind
{
    Percent,
    Amount,
}
