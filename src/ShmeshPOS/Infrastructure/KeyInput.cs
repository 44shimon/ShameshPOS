using System.Windows.Input;

namespace ShmeshPOS.Infrastructure;

public static class KeyInput
{
    /// <summary>Maps top-row and numpad digit keys to 0-9.</summary>
    public static int? ToDigit(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.None => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null,
    };
}
