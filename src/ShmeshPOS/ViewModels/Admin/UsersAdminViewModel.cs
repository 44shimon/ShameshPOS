using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Security;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

public partial class UserEditorViewModel : EditorViewModel
{
    public UserEditorViewModel(User? user, int currentUserId)
    {
        Id = user?.Id ?? 0;
        Heading = user?.DisplayName ?? "New User";
        IsSelf = user is not null && user.Id == currentUserId;
        _username = user?.Username ?? "";
        _displayName = user?.DisplayName ?? "";
        _role = user?.Role ?? UserRole.Cashier;
        _isActive = user?.IsActive ?? true;
        StartTracking();
    }

    public int Id { get; }
    public override bool IsNew => Id == 0;
    public string Heading { get; }

    /// <summary>Editing your own account: role and active status are locked so you cannot lock yourself out.</summary>
    public bool IsSelf { get; }
    public bool CanChangeAccess => !IsSelf;

    public IReadOnlyList<UserRole> Roles { get; } = Enum.GetValues<UserRole>();

    public string PinHint => IsNew
        ? $"{PinHasher.MinPinLength}-{PinHasher.MaxPinLength} digits, different from every other user's PIN."
        : "Leave blank to keep the current PIN.";

    [ObservableProperty] private string _username;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private UserRole _role;
    [ObservableProperty] private bool _isActive;

    // PIN fields are bound to masked boxes and discarded with the editor after saving.
    [ObservableProperty] private string _newPin = "";
    [ObservableProperty] private string _confirmPin = "";

    public (UserInput Input, string? NewPin) ToInput()
    {
        string? pin = null;
        if (NewPin.Length > 0 || ConfirmPin.Length > 0)
        {
            if (NewPin != ConfirmPin) throw new PosException("The two PIN entries don't match.");
            pin = NewPin;
        }
        return (new UserInput(Id, Username, DisplayName, Role, IsActive), pin);
    }
}

public class UsersAdminViewModel(IUserAdminService users, ISessionService session, IDialogService dialogs, ILogger<UsersAdminViewModel> logger)
    : AdminSectionViewModel<User, UserEditorViewModel>(dialogs, logger)
{
    public override string Title => "Users";
    public override string NewLabel => "New User";

    protected override Task<IReadOnlyList<User>> FetchAsync() => users.GetUsersAsync();

    protected override int IdOf(User item) => item.Id;
    protected override string NameOf(User item) => item.DisplayName;
    protected override bool IsActive(User item) => item.IsActive;

    protected override bool Matches(User item, string filter) =>
        item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || item.Username.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || item.Role.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);

    protected override UserEditorViewModel CreateEditor(User? item) => new(item, session.CurrentUser?.Id ?? 0);

    protected override async Task<int> SaveEditorAsync(UserEditorViewModel editor)
    {
        var (input, pin) = editor.ToInput();
        return (await users.SaveUserAsync(input, pin)).Id;
    }
}
