using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Views;

public partial class DialogView : UserControl
{
    public DialogView()
    {
        InitializeComponent();
        // Prompts take typing straight away.
        Loaded += (_, _) =>
        {
            if (DataContext is DialogViewModel { HasInput: true })
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(Input));
        };
    }
}
