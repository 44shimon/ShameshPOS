namespace ShmeshPOS.Services;

public readonly record struct SaleTotals(decimal Subtotal, decimal Tax, decimal Total);

/// <summary>
/// The single source of truth for sale arithmetic, shared by the cart display and the sale service so
/// what the cashier sees is exactly what gets saved. Prices are tax exclusive; tax is rounded per line.
/// </summary>
public static class SaleCalculator
{
    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal LineTotal(decimal unitPrice, int quantity) => RoundMoney(unitPrice * quantity);

    public static decimal LineTax(decimal lineTotal, decimal taxRate) => RoundMoney(lineTotal * taxRate);

    public static SaleTotals Calculate(IEnumerable<(decimal UnitPrice, int Quantity, decimal TaxRate)> lines)
    {
        decimal subtotal = 0, tax = 0;
        foreach (var (unitPrice, quantity, taxRate) in lines)
        {
            var lineTotal = LineTotal(unitPrice, quantity);
            subtotal += lineTotal;
            tax += LineTax(lineTotal, taxRate);
        }
        return new SaleTotals(subtotal, tax, subtotal + tax);
    }

    /// <summary>Change due for a cash payment, or null when the cash received does not cover the total.</summary>
    public static decimal? CalculateChange(decimal total, decimal cashReceived) =>
        cashReceived >= total ? cashReceived - total : null;
}
