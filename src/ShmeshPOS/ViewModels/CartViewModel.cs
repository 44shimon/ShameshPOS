using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class CartLineViewModel(Product product) : ObservableObject
{
    public int ProductId { get; } = product.Id;
    public string Name { get; } = product.Name;
    public decimal UnitPrice { get; } = product.Price;
    public decimal TaxRate { get; } = product.TaxRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal), nameof(DiscountAmount), nameof(DiscountText))]
    private int _quantity = 1;

    /// <summary>Discount on this line, as entered (a percentage follows the quantity).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal), nameof(DiscountAmount), nameof(DiscountText), nameof(HasDiscount))]
    private Discount? _discount;

    [ObservableProperty]
    private bool _isSelected;

    public decimal Gross => SaleCalculator.LineTotal(UnitPrice, Quantity);
    public decimal DiscountAmount => Discount?.AmountOff(Gross) ?? 0m;
    public bool HasDiscount => Discount is not null;

    /// <summary>The line's price after its own discount (a whole-sale discount is shown separately).</summary>
    public decimal LineTotal => Gross - DiscountAmount;

    /// <summary>e.g. "10% off  −$0.40".</summary>
    public string? DiscountText => Discount is { } d ? $"{d.Label}  −{DiscountAmount:C}" : null;
}

/// <summary>The in-progress sale. Pure in-memory state; nothing touches the database until payment.</summary>
public partial class CartViewModel : ObservableObject
{
    public ObservableCollection<CartLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private CartLineViewModel? _selectedLine;

    /// <summary>Discount on the whole sale, as entered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaleDiscount))]
    private Discount? _saleDiscount;

    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _tax;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private int _itemCount;

    /// <summary>What the whole-sale discount takes off.</summary>
    [ObservableProperty] private decimal _saleDiscountAmount;

    /// <summary>Line discounts plus the whole-sale discount.</summary>
    [ObservableProperty] private decimal _discountTotal;

    public bool IsEmpty => Lines.Count == 0;
    public bool HasSaleDiscount => SaleDiscount is not null;
    public bool HasDiscounts => Lines.Any(l => l.HasDiscount) || HasSaleDiscount;

    partial void OnSelectedLineChanged(CartLineViewModel? oldValue, CartLineViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    partial void OnSaleDiscountChanged(Discount? value) => Recalculate();

    /// <returns>The line that was added or incremented.</returns>
    public CartLineViewModel Add(Product product, int quantity = 1)
    {
        var line = Lines.FirstOrDefault(l => l.ProductId == product.Id);
        if (line is null)
        {
            line = new CartLineViewModel(product) { Quantity = Math.Min(quantity, SaleService.MaxLineQuantity) };
            Lines.Add(line);
        }
        else
        {
            line.Quantity = Math.Min(line.Quantity + quantity, SaleService.MaxLineQuantity);
        }

        SelectedLine = line;
        Recalculate();
        return line;
    }

    public void Increase(CartLineViewModel line) => SetQuantity(line, line.Quantity + 1);

    public void Decrease(CartLineViewModel line) => SetQuantity(line, line.Quantity - 1);

    /// <summary>Sets a line's quantity; zero or less removes the line.</summary>
    public void SetQuantity(CartLineViewModel line, int quantity)
    {
        if (quantity <= 0)
        {
            Remove(line);
            return;
        }
        line.Quantity = Math.Min(quantity, SaleService.MaxLineQuantity);
        SelectedLine = line;
        Recalculate();
    }

    /// <summary>Sets or (with null) removes a line's discount.</summary>
    public void SetDiscount(CartLineViewModel line, Discount? discount)
    {
        line.Discount = discount;
        Recalculate();
    }

    public void Remove(CartLineViewModel line)
    {
        var index = Lines.IndexOf(line);
        if (index < 0) return;

        Lines.RemoveAt(index);
        SelectedLine = Lines.Count == 0 ? null : Lines[Math.Min(index, Lines.Count - 1)];
        Recalculate();
    }

    public void Clear()
    {
        Lines.Clear();
        SelectedLine = null;
        SaleDiscount = null;
        Recalculate();
    }

    /// <summary>Drops every discount, e.g. when a cashier retrieves a held sale an administrator discounted.</summary>
    public void ClearDiscounts()
    {
        foreach (var line in Lines) line.Discount = null;
        SaleDiscount = null;
        Recalculate();
    }

    public IReadOnlyList<SaleLineInput> ToSaleLines() =>
        Lines.Select(l => new SaleLineInput(l.ProductId, l.Name, l.UnitPrice, l.TaxRate, l.Quantity, l.Discount)).ToList();

    private void Recalculate()
    {
        var totals = SaleCalculator.Calculate(ToSaleLines(), SaleDiscount);
        Subtotal = totals.Subtotal;
        Tax = totals.Tax;
        Total = totals.Total;
        SaleDiscountAmount = totals.SaleDiscount;
        DiscountTotal = totals.Discount;
        ItemCount = Lines.Sum(l => l.Quantity);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasDiscounts));
    }
}
