using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

/// <summary>View-only focus handling: the search box in search mode, the amount box in payment mode.</summary>
public partial class CustomerLookupView : UserControl
{
    public CustomerLookupView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusActiveBox();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomerLookupViewModel.IsPaymentMode) or nameof(CustomerLookupViewModel.PaymentAmountText))
            FocusActiveBox();
    }

    private void FocusActiveBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var box = DataContext is CustomerLookupViewModel { IsPaymentMode: true } ? AmountBox : SearchBox;
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        });
}
