using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class LoginViewModel(
    IAuthenticationService auth,
    IOptions<PosOptions> options,
    ILogger<LoginViewModel> logger) : ObservableObject, IKeyboardHandler
{
    // The PIN lives only in this field and is cleared after every attempt. It is never bound to the UI.
    private string _pin = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    public string StoreName => options.Value.StoreName;

    /// <summary>One dot per digit entered, with empty placeholders up to the minimum PIN length.</summary>
    public string MaskedPin
    {
        get
        {
            var shown = Math.Max(_pin.Length, PinHasher.MinPinLength);
            return string.Join(" ", Enumerable.Range(0, shown).Select(i => i < _pin.Length ? "●" : "○"));
        }
    }

    public event EventHandler<User>? LoggedIn;

    [RelayCommand]
    private void Keypad(string key)
    {
        switch (key)
        {
            case "CLR":
                SetPin("");
                break;
            case "BKSP":
                if (_pin.Length > 0) SetPin(_pin[..^1]);
                break;
            default:
                if (_pin.Length < PinHasher.MaxPinLength && key.Length == 1 && char.IsAsciiDigit(key[0]))
                {
                    ErrorMessage = null;
                    SetPin(_pin + key);
                }
                break;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        SetPin("");
        ErrorMessage = null;
    }

    private bool CanLogin() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        if (_pin.Length == 0)
        {
            ErrorMessage = "Enter your PIN.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        var pin = _pin;
        SetPin("");
        try
        {
            var result = await auth.LoginWithPinAsync(pin);
            if (result.Success)
                LoggedIn?.Invoke(this, result.User!);
            else
                ErrorMessage = result.ErrorMessage;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed with an unexpected error");
            ErrorMessage = "Unable to log in. Check the connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetPin(string value)
    {
        _pin = value;
        OnPropertyChanged(nameof(MaskedPin));
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (IsBusy) return true;

        if (KeyInput.ToDigit(key) is { } digit)
        {
            Keypad(digit.ToString());
            return true;
        }

        switch (key)
        {
            case Key.Back:
                Keypad("BKSP");
                return true;
            case Key.Escape:
            case Key.Delete:
                Clear();
                return true;
            case Key.Enter:
                LoginCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }
}
