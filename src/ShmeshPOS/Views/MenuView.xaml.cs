using System.Windows.Controls;
using System.Windows.Input;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

public partial class MenuView : UserControl
{
    public MenuView() => InitializeComponent();

    private void OnOverlayClicked(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, OverlayRoot) && DataContext is MenuViewModel menu)
            menu.Close();
    }
}
