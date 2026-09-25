using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShmeshPOS.Controls;

/// <summary>Touch keypad. The bottom-left key is "." when <see cref="ShowDecimal"/> is true, otherwise "CLR".</summary>
public partial class NumericKeypad : UserControl
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(NumericKeypad));

    public static readonly DependencyProperty ShowDecimalProperty =
        DependencyProperty.Register(nameof(ShowDecimal), typeof(bool), typeof(NumericKeypad),
            new PropertyMetadata(false, (d, _) => ((NumericKeypad)d).UpdateLeftKey()));

    public NumericKeypad()
    {
        InitializeComponent();
        UpdateLeftKey();
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool ShowDecimal
    {
        get => (bool)GetValue(ShowDecimalProperty);
        set => SetValue(ShowDecimalProperty, value);
    }

    private void UpdateLeftKey()
    {
        LeftKey.Content = ShowDecimal ? "." : "CLR";
        LeftKey.CommandParameter = ShowDecimal ? "." : "CLR";
    }
}
