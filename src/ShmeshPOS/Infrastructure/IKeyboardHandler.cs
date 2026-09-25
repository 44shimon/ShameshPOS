using System.Windows.Input;

namespace ShmeshPOS.Infrastructure;

/// <summary>
/// Implemented by view models that respond to POS shortcut keys. The main window forwards every key
/// press (before controls see it) to the active view model, which returns true when it consumed the key.
/// </summary>
public interface IKeyboardHandler
{
    /// <param name="key">The pressed key.</param>
    /// <param name="textInputFocused">True when a text box has focus, so editing keys (Delete, digits, +/-) belong to it.</param>
    bool HandleKey(Key key, bool textInputFocused);
}

/// <summary>Lets view models request an application exit without referencing WPF's Application.</summary>
public interface IApplicationControl
{
    void ExitApplication();
}
