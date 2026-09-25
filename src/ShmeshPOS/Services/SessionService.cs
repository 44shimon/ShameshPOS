using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public interface ISessionService
{
    User? CurrentUser { get; }
    bool IsSignedIn { get; }
    void SignIn(User user);
    void SignOut();
    bool HasPermission(Permission permission);
}

public class SessionService : ISessionService
{
    public User? CurrentUser { get; private set; }
    public bool IsSignedIn => CurrentUser is not null;

    public void SignIn(User user) => CurrentUser = user;
    public void SignOut() => CurrentUser = null;

    public bool HasPermission(Permission permission) =>
        CurrentUser is { } user && RolePermissions.Has(user.Role, permission);
}
