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

public partial class LoginUserViewModel(LoginUser user) : ObservableObject
{
    public LoginUser User { get; } = user;
    public string DisplayName => User.DisplayName;
    public string RoleText => User.Role == UserRole.Administrator ? "Administrator" : "Cashier";

    /// <summary>First letters of the name, for the round badge on the button.</summary>
    public string Initials => string.Concat(User.DisplayName
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Login: tap your name, then type your PIN on the keypad (or keyboard).</summary>
public partial class LoginViewModel(
    IAuthenticationService auth,
    IOptions<PosOptions> options,
    ILogger<LoginViewModel> logger) : ObservableObject, IKeyboardHandler
{
    // The PIN lives only in this field and is cleared after every attempt. It is never bound to the UI.
    private string _pin = "";

    [ObservableProperty]
    private IReadOnlyList<LoginUserViewModel> _users = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedUser), nameof(Prompt))]
    private LoginUserViewModel? _selectedUser;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    public string StoreName => options.Value.StoreName;
    public bool HasSelectedUser => SelectedUser is not null;
    public string Prompt => SelectedUser is { } u ? $"Hi {u.DisplayName}, enter your PIN" : "Tap your name";

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

    public async Task InitializeAsync()
    {
        try
        {
            var users = await auth.GetLoginUsersAsync();
            Users = users.Select(u => new LoginUserViewModel(u)).ToList();
            // A single user needs no choosing.
            if (Users.Count == 1) SelectUser(Users[0]);
            if (Users.Count == 0) ErrorMessage = "No active users. Ask an administrator.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load the user list for login");
            ErrorMessage = "Unable to load users. Check the connection.";
        }
    }

    [RelayCommand]
    private void SelectUser(LoginUserViewModel? user)
    {
        if (user is null || IsBusy) return;
        foreach (var u in Users) u.IsSelected = u == user;
        SelectedUser = user;
        SetPin("");
        ErrorMessage = null;
    }

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
        if (SelectedUser is null)
        {
            ErrorMessage = "Tap your name first.";
            return;
        }
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
            var result = await auth.LoginAsync(SelectedUser.User.Id, pin);
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

    private void MoveSelection(int direction)
    {
        if (Users.Count == 0) return;
        var index = SelectedUser is null ? (direction > 0 ? -1 : Users.Count) : Users.ToList().IndexOf(SelectedUser);
        SelectUser(Users[Math.Clamp(index + direction, 0, Users.Count - 1)]);
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
            case Key.Up or Key.Left:
                MoveSelection(-1);
                return true;
            case Key.Down or Key.Right or Key.Tab:
                MoveSelection(1);
                return true;
            default:
                return false;
        }
    }
}
