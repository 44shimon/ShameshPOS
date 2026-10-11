using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

public partial class PaymentView : UserControl
{
    public PaymentView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusAmountBox();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaymentViewModel.SelectedMethod) or nameof(PaymentViewModel.AmountText))
            FocusAmountBox();
    }

    /// <summary>Keeps the amount box focused so the cashier can just type the amount and press Enter.</summary>
    private void FocusAmountBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (DataContext is PaymentViewModel { SelectedMethod: not null })
            {
                Keyboard.Focus(AmountBox);
                AmountBox.CaretIndex = AmountBox.Text.Length;
            }
            else
            {
                // Park focus on the view so stray keys don't reach the search box underneath.
                Focusable = true;
                Keyboard.Focus(this);
            }
        });
}
