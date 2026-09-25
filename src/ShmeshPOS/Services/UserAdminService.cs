using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public sealed record UserInput(int Id, string Username, string DisplayName, UserRole Role, bool IsActive);

public interface IUserAdminService
{
    Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the user when <see cref="UserInput.Id"/> is 0, otherwise updates it. <paramref name="newPin"/>
    /// is required for a new user; for an existing user null keeps the current PIN.
    /// </summary>
    Task<User> SaveUserAsync(UserInput input, string? newPin, CancellationToken cancellationToken = default);
}

public partial class UserAdminService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    TimeProvider clock,
    ILogger<UserAdminService> logger) : IUserAdminService
{
    [GeneratedRegex("^[A-Za-z0-9._-]{2,50}$")]
    private static partial Regex UsernamePattern();

    public async Task<IReadOnlyList<User>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        RequirePermission();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<User> SaveUserAsync(UserInput input, string? newPin, CancellationToken cancellationToken = default)
    {
        var admin = RequirePermission();

        var username = input.Username.Trim();
        if (!UsernamePattern().IsMatch(username))
            throw new PosException("Username must be 2-50 characters: letters, numbers, dot, dash or underscore.");
        var displayName = input.DisplayName.Trim();
        if (displayName.Length is 0 or > 100)
            throw new PosException("Enter a display name (up to 100 characters).");

        if (input.Id == 0 && newPin is null)
            throw new PosException("Set a PIN for the new user.");
        if (newPin is not null)
        {
            if (!PinHasher.IsValidPinFormat(newPin))
                throw new PosException($"PIN must be {PinHasher.MinPinLength}-{PinHasher.MaxPinLength} digits.");
            if (PinHasher.IsWeakPin(newPin))
                throw new PosException("That PIN is too easy to guess. Avoid repeated or sequential digits.");
        }

        // An administrator may not lock themselves out.
        if (input.Id == admin.Id && (!input.IsActive || input.Role != UserRole.Administrator))
            throw new PosException("You cannot disable your own account or remove your own administrator role.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var lower = username.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Id != input.Id && u.Username.ToLower() == lower, cancellationToken))
            throw new PosException($"The username “{username}” is already taken.");

        User user;
        if (input.Id == 0)
        {
            user = new User { Username = username, DisplayName = displayName, PinHash = "", CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.Users.Add(user);
        }
        else
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == input.Id, cancellationToken)
                ?? throw new PosException("That user no longer exists.");

            var losesAdmin = user is { Role: UserRole.Administrator, IsActive: true } &&
                             (input.Role != UserRole.Administrator || !input.IsActive);
            if (losesAdmin && !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsActive && u.Role == UserRole.Administrator, cancellationToken))
                throw new PosException("At least one active administrator is required.");
        }

        if (newPin is not null)
        {
            // Login is PIN-only, so a PIN must identify exactly one person. Check every other account's hash.
            var others = await db.Users.AsNoTracking().Where(u => u.Id != input.Id).Select(u => u.PinHash).ToListAsync(cancellationToken);
            if (await Task.Run(() => others.Any(hash => PinHasher.Verify(newPin, hash)), cancellationToken))
                throw new PosException("That PIN is already in use. Choose a different PIN.");
            user.PinHash = PinHasher.Hash(newPin);
        }

        var before = input.Id == 0 ? "new" : $"username={user.Username}, name={user.DisplayName}, role={user.Role}, active={user.IsActive}";
        user.Username = username;
        user.DisplayName = displayName;
        user.Role = input.Role;
        user.IsActive = input.IsActive;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PosException($"The username “{username}” is already taken.", ex);
        }

        // Never log the PIN; only whether it changed.
        logger.LogInformation(
            "Admin {AdminId} {Action} user {UserId} ({Before} → username={Username}, name={DisplayName}, role={Role}, active={IsActive}); PIN changed: {PinChanged}",
            admin.Id, input.Id == 0 ? "created" : "updated", user.Id, before, user.Username, user.DisplayName, user.Role, user.IsActive, newPin is not null);

        if (user.Id == admin.Id)
            session.SignIn(user);

        return user;
    }

    private User RequirePermission()
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(Permission.ManageUsers))
            throw new PosException("You are not allowed to manage users.");
        return user;
    }
}
