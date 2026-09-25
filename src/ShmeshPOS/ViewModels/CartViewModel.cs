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
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    private int _quantity = 1;

    [ObservableProperty]
    private bool _isSelected;

    public decimal LineTotal => SaleCalculator.LineTotal(UnitPrice, Quantity);
}

/// <summary>The in-progress sale. Pure in-memory state; nothing touches the database until payment.</summary>
public partial class CartViewModel : ObservableObject
{
    public ObservableCollection<CartLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private CartLineViewModel? _selectedLine;

    [ObservableProperty] private decimal _subtotal;
    [ObservableProperty] private decimal _tax;
    [ObservableProperty] private decimal _total;
    [ObservableProperty] private int _itemCount;

    public bool IsEmpty => Lines.Count == 0;

    partial void OnSelectedLineChanged(CartLineViewModel? oldValue, CartLineViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

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
        Recalculate();
    }

    public IReadOnlyList<SaleLineInput> ToSaleLines() =>
        Lines.Select(l => new SaleLineInput(l.ProductId, l.Name, l.UnitPrice, l.TaxRate, l.Quantity)).ToList();

    private void Recalculate()
    {
        var totals = SaleCalculator.Calculate(Lines.Select(l => (l.UnitPrice, l.Quantity, l.TaxRate)));
        Subtotal = totals.Subtotal;
        Tax = totals.Tax;
        Total = totals.Total;
        ItemCount = Lines.Sum(l => l.Quantity);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
