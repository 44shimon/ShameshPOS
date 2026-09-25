using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

/// <summary>View-only plumbing: keyboard focus and cart scrolling. No business logic lives here.</summary>
public partial class PosView : UserControl
{
    private PosViewModel? _viewModel;

    public PosView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusSearch();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested -= OnFocusSearchRequested;
            _viewModel.Cart.Lines.CollectionChanged -= OnCartLinesChanged;
        }

        _viewModel = e.NewValue as PosViewModel;
        if (_viewModel is not null)
        {
            _viewModel.FocusSearchRequested += OnFocusSearchRequested;
            _viewModel.Cart.Lines.CollectionChanged += OnCartLinesChanged;
        }
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e) => FocusSearch();

    private void FocusSearch() =>
        // Deferred so it runs after the click or overlay change that triggered it has finished.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.CaretIndex = SearchBox.Text.Length;
        });

    private void OnCartLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            CartScroll.ScrollToEnd();
    }
}
