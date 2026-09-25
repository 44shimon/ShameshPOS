using System.Windows;
using System.Windows.Controls;

namespace ShmeshPOS.Controls;

/// <summary>
/// Makes <see cref="PasswordBox.Password"/> bindable (WPF deliberately doesn't), for masked PIN entry in forms.
/// Usage: <c>controls:PasswordBoxBinding.Password="{Binding NewPin}"</c>.
/// </summary>
public static class PasswordBoxBinding
{
    // Default is null so the first binding value ("") still raises the callback and wires up the handler.
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(PasswordBoxBinding));

    public static string? GetPassword(DependencyObject d) => (string?)d.GetValue(PasswordProperty);
    public static void SetPassword(DependencyObject d, string? value) => d.SetValue(PasswordProperty, value);

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        box.PasswordChanged -= OnBoxPasswordChanged;
        if (!(bool)box.GetValue(IsUpdatingProperty))
            box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += OnBoxPasswordChanged;
    }

    private static void OnBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
