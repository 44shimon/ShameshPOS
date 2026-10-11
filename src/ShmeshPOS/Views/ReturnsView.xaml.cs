using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

/// <summary>Keeps the receipt box focused while looking up a sale, so a receipt barcode scan lands in it.</summary>
public partial class ReturnsView : UserControl
{
    public ReturnsView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusReceiptBox();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnChanged;
            if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnChanged;
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReturnsViewModel.IsFindStage)) FocusReceiptBox();
    }

    private void FocusReceiptBox() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (DataContext is ReturnsViewModel { IsFindStage: true })
            {
                Keyboard.Focus(ReceiptBox);
                ReceiptBox.SelectAll();
            }
            else
            {
                Focusable = true;
                Keyboard.Focus(this);
            }
        });
}
