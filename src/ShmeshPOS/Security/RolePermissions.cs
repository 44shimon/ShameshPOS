using ShmeshPOS.Models;

namespace ShmeshPOS.Security;

/// <summary>
/// Maps roles to permissions. Screens check permissions, never roles, so adding a role
/// (e.g. Supervisor) only means adding an entry here.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, HashSet<Permission>> Map =
        new Dictionary<UserRole, HashSet<Permission>>
        {
            [UserRole.Cashier] =
            [
                Permission.Sell,
                Permission.TakePayment,
                Permission.ViewOwnTransactions,
                Permission.UseCustomerAccounts,
            ],
            [UserRole.Administrator] = [.. Enum.GetValues<Permission>()],
        };

    public static bool Has(UserRole role, Permission permission) =>
        Map.TryGetValue(role, out var permissions) && permissions.Contains(permission);
}
