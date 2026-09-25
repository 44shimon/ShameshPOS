using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

/// <summary>Keypad overlay for typing a quantity for the selected cart line. Zero removes the line.</summary>
public partial class QuantityEntryViewModel(CartLineViewModel line) : ObservableObject, IKeyboardHandler
{
    public CartLineViewModel Line { get; } = line;

    [ObservableProperty]
    private string _text = "";

    /// <summary>Raised with the new quantity, or null when cancelled.</summary>
    public event EventHandler<int?>? Closed;

    [RelayCommand]
    private void Keypad(string key)
    {
        Text = key switch
        {
            "BKSP" => Text.Length > 0 ? Text[..^1] : "",
            "CLR" => "",
            _ when Text.Length >= SaleService.MaxLineQuantity.ToString().Length => Text,
            _ => (Text + key).TrimStart('0') is { Length: > 0 } t ? t : "0",
        };
    }

    [RelayCommand]
    private void Confirm()
    {
        if (int.TryParse(Text, out var quantity))
            Closed?.Invoke(this, quantity);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(this, null);

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (KeyInput.ToDigit(key) is { } digit)
            Keypad(digit.ToString());
        else if (key == Key.Back)
            Keypad("BKSP");
        else if (key == Key.Enter)
            Confirm();
        else if (key == Key.Escape)
            Cancel();
        return true;
    }
}
