using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

/// <summary>What the discount overlay decided: the target (a line, or the whole sale when null) and the discount (null removes it).</summary>
public sealed record DiscountChoice(CartLineViewModel? Line, Discount? Discount);

/// <summary>
/// Discount overlay (F6): a percentage or an amount off the selected item or the whole sale, typed on the keypad
/// or picked from the common percentages.
/// </summary>
public partial class DiscountViewModel : ObservableObject, IKeyboardHandler
{
    private readonly CartViewModel _cart;

    public DiscountViewModel(CartViewModel cart)
    {
        _cart = cart;
        Line = cart.SelectedLine;
        _isWholeSale = Line is null;
        LoadCurrent();
    }

    /// <summary>The selected item, if any; it can be discounted on its own.</summary>
    public CartLineViewModel? Line { get; }
    public bool HasLine => Line is not null;

    public IReadOnlyList<decimal> QuickPercents { get; } = [5m, 10m, 15m, 20m, 25m, 50m];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetText), nameof(BasePrice), nameof(AmountOff), nameof(Problem), nameof(CanApply), nameof(HasCurrent))]
    private bool _isWholeSale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Discount), nameof(AmountOff), nameof(Problem), nameof(CanApply))]
    private bool _isPercent = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Discount), nameof(AmountOff), nameof(Problem), nameof(CanApply))]
    private string _valueText = "";

    public string TargetText => IsWholeSale ? "Whole sale" : Line?.Name ?? "";

    /// <summary>The price the discount comes off: the line's price, or the sale after any line discounts (before tax).</summary>
    public decimal BasePrice => IsWholeSale ? _cart.Lines.Sum(l => l.LineTotal) : Line?.Gross ?? 0m;

    public bool HasCurrent => IsWholeSale ? _cart.SaleDiscount is not null : Line?.Discount is not null;

    public Discount? Discount =>
        decimal.TryParse(ValueText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            ? new Discount(IsPercent ? DiscountKind.Percent : DiscountKind.Amount, value)
            : null;

    public decimal AmountOff => Discount is { Problem: null } d ? d.AmountOff(BasePrice) : 0m;

    public string? Problem => ValueText.Length == 0 ? null
        : Discount is not { } d ? "Enter a number."
        : d.Problem ?? (d.Kind == DiscountKind.Amount && d.Value > BasePrice ? $"That's more than the {BasePrice:C} it comes off." : null);

    public bool CanApply => Discount is not null && Problem is null;

    /// <summary>Raised with the choice, or null when cancelled.</summary>
    public event EventHandler<DiscountChoice?>? Closed;

    partial void OnIsWholeSaleChanged(bool value) => LoadCurrent();

    /// <summary>Shows the target's current discount, so it can be changed.</summary>
    private void LoadCurrent()
    {
        var current = IsWholeSale ? _cart.SaleDiscount : Line?.Discount;
        IsPercent = current?.Kind != DiscountKind.Amount;
        ValueText = current?.Value.ToString(IsPercent ? "0.##" : "0.00", CultureInfo.CurrentCulture) ?? "";
    }

    [RelayCommand]
    private void SetTarget(string target)
    {
        if (target == "Item" && !HasLine) return;
        IsWholeSale = target != "Item";
    }

    [RelayCommand]
    private void SetKind(string kind)
    {
        IsPercent = kind == "Percent";
        ValueText = "";
    }

    [RelayCommand]
    private void QuickPercent(decimal percent)
    {
        IsPercent = true;
        ValueText = percent.ToString("0.##", CultureInfo.CurrentCulture);
        Apply();
    }

    [RelayCommand]
    private void Keypad(string key)
    {
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        ValueText = key switch
        {
            "BKSP" => ValueText.Length > 0 ? ValueText[..^1] : "",
            "CLR" => "",
            "." when ValueText.Contains(separator) => ValueText,
            "." => (ValueText.Length == 0 ? "0" : ValueText) + separator,
            _ when ValueText.Length >= 8 => ValueText,
            _ => ValueText + key,
        };
    }

    [RelayCommand]
    private void Apply()
    {
        if (CanApply) Closed?.Invoke(this, new DiscountChoice(IsWholeSale ? null : Line, Discount));
    }

    [RelayCommand]
    private void Remove() => Closed?.Invoke(this, new DiscountChoice(IsWholeSale ? null : Line, null));

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, null);

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (KeyInput.ToDigit(key) is { } digit)
            Keypad(digit.ToString());
        else switch (key)
        {
            case Key.Back: Keypad("BKSP"); break;
            case Key.Decimal or Key.OemPeriod: Keypad("."); break;
            case Key.Enter: Apply(); break;
            case Key.Escape or Key.F6: Cancel(); break;
        }
        return true;
    }
}
