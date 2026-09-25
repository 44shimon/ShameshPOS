using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly bool _kioskMode;

    public MainWindow(MainWindowViewModel viewModel, PosOptions options)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Title = options.StoreName;

        _kioskMode = options.FullScreen;
        if (_kioskMode)
        {
            // Borderless full screen. Not Topmost and no keyboard hooks: Windows itself is never locked.
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Set by the app when an authorised exit is requested, so the kiosk close guard lets it through.</summary>
    public bool AllowClose { get; set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // System keys (Alt+…) keep their normal behaviour, except Alt+F4 in kiosk mode (see OnClosing).
        if (e.Key == Key.System) return;

        var textInputFocused = Keyboard.FocusedElement is TextBox;
        if (_viewModel.HandleKey(e.Key, textInputFocused))
            e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // In kiosk mode only the F3 → Exit menu item (administrators) may close the POS.
        if (_kioskMode && !AllowClose)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
