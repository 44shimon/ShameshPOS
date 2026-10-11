using System.Globalization;
using ShmeshPOS.Models;

namespace ShmeshPOS.Services;

public readonly record struct SaleTotals(decimal Subtotal, decimal Tax, decimal Total);

/// <summary>A discount as the cashier entered it: a percentage, or a fixed amount off (tax exclusive).</summary>
public sealed record Discount(DiscountKind Kind, decimal Value)
{
    public const decimal MaxAmount = 99_999_999.99m;

    public static Discount Percent(decimal percent) => new(DiscountKind.Percent, percent);
    public static Discount Amount(decimal amount) => new(DiscountKind.Amount, amount);

    /// <summary>e.g. "10% off" or "$1.00 off".</summary>
    public string Label => Kind == DiscountKind.Percent
        ? $"{Value.ToString("0.##", CultureInfo.CurrentCulture)}% off"
        : $"{Value.ToString("C", CultureInfo.CurrentCulture)} off";

    /// <summary>Why the value can't be used, or null when it is valid.</summary>
    public string? Problem => Kind switch
    {
        DiscountKind.Percent when Value is <= 0 or > 100 => "Enter a percentage between 0 and 100.",
        DiscountKind.Percent when decimal.Round(Value, 2) != Value => "A percentage can have at most 2 decimal places.",
        DiscountKind.Amount when Value is <= 0 or > MaxAmount => "Enter an amount greater than zero.",
        DiscountKind.Amount when decimal.Round(Value, 2) != Value => "An amount can have at most 2 decimal places.",
        _ => null,
    };

    /// <summary>How much comes off <paramref name="price"/>; never more than the price itself.</summary>
    public decimal AmountOff(decimal price)
    {
        if (price <= 0) return 0m;
        var off = Kind == DiscountKind.Percent ? SaleCalculator.RoundMoney(price * Value / 100m) : Value;
        return Math.Min(price, Math.Max(0m, off));
    }
}

/// <param name="Gross">Quantity × unit price.</param>
/// <param name="LineDiscount">Taken off by the line's own discount.</param>
/// <param name="SaleDiscountShare">The line's share of the whole-sale discount.</param>
/// <param name="Net">What is charged for the line before tax; tax is worked out on this.</param>
public sealed record LineAmounts(decimal Gross, decimal LineDiscount, decimal SaleDiscountShare, decimal Net, decimal Tax);

/// <summary>A sale worked out line by line, with discounts applied.</summary>
public sealed record SaleBreakdown(IReadOnlyList<LineAmounts> Lines, decimal Subtotal, decimal Tax, decimal Total)
{
    public decimal LineDiscounts => Lines.Sum(l => l.LineDiscount);
    public decimal SaleDiscount => Lines.Sum(l => l.SaleDiscountShare);
    public decimal Discount => LineDiscounts + SaleDiscount;
}

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

    /// <summary>
    /// Works out every line with its own discount, then spreads the whole-sale discount over the lines in
    /// proportion to what is left of each (to the cent), so tax is charged on the discounted price and a
    /// later partial return gives back the discounted price.
    /// </summary>
    public static SaleBreakdown Calculate(IReadOnlyList<SaleLineInput> lines, Discount? saleDiscount = null)
    {
        var gross = lines.Select(l => LineTotal(l.UnitPrice, l.Quantity)).ToList();
        var lineDiscounts = lines.Select((l, i) => l.Discount?.AmountOff(gross[i]) ?? 0m).ToList();
        var afterLineDiscounts = gross.Select((g, i) => g - lineDiscounts[i]).ToList();
        var saleOff = saleDiscount?.AmountOff(afterLineDiscounts.Sum()) ?? 0m;
        var shares = Allocate(saleOff, afterLineDiscounts);

        var amounts = lines.Select((l, i) =>
        {
            var net = afterLineDiscounts[i] - shares[i];
            return new LineAmounts(gross[i], lineDiscounts[i], shares[i], net, LineTax(net, l.TaxRate));
        }).ToList();
        var subtotal = amounts.Sum(a => a.Net);
        var tax = amounts.Sum(a => a.Tax);
        return new SaleBreakdown(amounts, subtotal, tax, subtotal + tax);
    }

    /// <summary>
    /// Splits <paramref name="amount"/> over <paramref name="weights"/> in proportion, in whole cents. The cents
    /// left over after rounding down go to the largest remainders (earlier lines first on a tie), so the shares
    /// always add up to exactly <paramref name="amount"/> and none is larger than its weight.
    /// </summary>
    public static IReadOnlyList<decimal> Allocate(decimal amount, IReadOnlyList<decimal> weights)
    {
        var shares = new decimal[weights.Count];
        var total = weights.Sum();
        if (amount <= 0 || total <= 0) return shares;

        var cents = (long)(RoundMoney(Math.Min(amount, total)) * 100m);
        var exact = weights.Select(w => cents * w / total).ToList();
        var whole = exact.Select(e => (long)Math.Floor(e)).ToList();
        var left = cents - whole.Sum();
        foreach (var i in Enumerable.Range(0, weights.Count).OrderByDescending(i => exact[i] - whole[i]).ThenBy(i => i).Take((int)left))
            whole[i]++;

        for (var i = 0; i < shares.Length; i++) shares[i] = whole[i] / 100m;
        return shares;
    }

    /// <summary>Change due for a cash payment, or null when the cash received does not cover the total.</summary>
    public static decimal? CalculateChange(decimal total, decimal cashReceived) =>
        cashReceived >= total ? cashReceived - total : null;
}
