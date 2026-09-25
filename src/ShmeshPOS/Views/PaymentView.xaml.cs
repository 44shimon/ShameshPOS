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
        Loaded += (_, _) => FocusCashBox();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaymentViewModel.IsCash) or nameof(PaymentViewModel.CashReceivedText))
            FocusCashBox();
    }

    /// <summary>Keeps the cash box focused so the cashier can just type the amount and press Enter.</summary>
    private void FocusCashBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (DataContext is PaymentViewModel { IsCash: true })
            {
                Keyboard.Focus(CashBox);
                CashBox.CaretIndex = CashBox.Text.Length;
            }
            else
            {
                // Park focus on the view so stray keys don't reach the search box underneath.
                Focusable = true;
                Keyboard.Focus(this);
            }
        });
}
