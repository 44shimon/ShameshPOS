namespace ShmeshPOS.Models;

/// <summary>
/// Roles are stored by name in the database, so new roles can be appended without
/// renumbering existing rows. Permissions for each role live in <see cref="Security.RolePermissions"/>.
/// </summary>
public enum UserRole
{
    Cashier,
    Administrator,
}
