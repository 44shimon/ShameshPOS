using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public sealed record LoginResult(bool Success, User? User, string? ErrorMessage)
{
    public static LoginResult Ok(User user) => new(true, user, null);
    public static LoginResult Fail(string message) => new(false, null, message);
}

/// <summary>A name button on the login screen. Carries nothing secret.</summary>
public sealed record LoginUser(int Id, string DisplayName, UserRole Role);

public interface IAuthenticationService
{
    /// <summary>Active users, for the "who's working?" buttons on the login screen.</summary>
    Task<IReadOnlyList<LoginUser>> GetLoginUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks the PIN of the chosen user only.</summary>
    Task<LoginResult> LoginAsync(int userId, string pin, CancellationToken cancellationToken = default);

    /// <summary>PIN-only login: finds the active user whose PIN matches.</summary>
    Task<LoginResult> LoginWithPinAsync(string pin, CancellationToken cancellationToken = default);

    void Logout();
}

public class AuthenticationService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<AuthenticationService> logger) : IAuthenticationService
{
    private readonly object _gate = new();
    private int _failedAttempts;
    private DateTimeOffset? _lockedUntil;

    public async Task<IReadOnlyList<LoginUser>> GetLoginUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new LoginUser(u.Id, u.DisplayName, u.Role))
            .ToListAsync(cancellationToken);
    }

    public async Task<LoginResult> LoginAsync(int userId, string pin, CancellationToken cancellationToken = default)
    {
        if (LockoutMessage() is { } locked)
            return LoginResult.Fail(locked);
        if (!PinHasher.IsValidPinFormat(pin))
            return RegisterFailure($"PIN format invalid for user {userId}");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
            return RegisterFailure($"user {userId} not found or inactive");

        // PBKDF2 is deliberately slow, so verify off the UI thread.
        if (!await Task.Run(() => PinHasher.Verify(pin, user.PinHash), cancellationToken))
            return RegisterFailure($"wrong PIN for user {userId}");

        return Succeed(user);
    }

    public async Task<LoginResult> LoginWithPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        if (LockoutMessage() is { } locked)
            return LoginResult.Fail(locked);
        if (!PinHasher.IsValidPinFormat(pin))
            return RegisterFailure("PIN format invalid");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);

        // PIN-only login: compare against each active user. PBKDF2 is deliberately slow, so run it off the UI thread.
        var user = await Task.Run(() => candidates.FirstOrDefault(u => PinHasher.Verify(pin, u.PinHash)), cancellationToken);
        if (user is null)
            return RegisterFailure("no matching user");

        return Succeed(user);
    }

    private LoginResult Succeed(User user)
    {
        lock (_gate)
        {
            _failedAttempts = 0;
            _lockedUntil = null;
        }

        session.SignIn(user);
        logger.LogInformation("Login succeeded for user {UserId} ({Username}, {Role})", user.Id, user.Username, user.Role);
        return LoginResult.Ok(user);
    }

    private string? LockoutMessage()
    {
        var remaining = GetRemainingLockout();
        if (remaining <= TimeSpan.Zero) return null;
        logger.LogWarning("Login rejected: terminal locked out for {Seconds}s", (int)remaining.TotalSeconds);
        return $"Too many attempts. Try again in {(int)Math.Ceiling(remaining.TotalSeconds)} seconds.";
    }

    public void Logout()
    {
        if (session.CurrentUser is { } user)
            logger.LogInformation("User {UserId} ({Username}) logged out", user.Id, user.Username);
        session.SignOut();
    }

    private TimeSpan GetRemainingLockout()
    {
        lock (_gate)
        {
            return _lockedUntil is { } until ? until - clock.GetUtcNow() : TimeSpan.Zero;
        }
    }

    private LoginResult RegisterFailure(string reason)
    {
        int attempts;
        bool lockedOut = false;
        lock (_gate)
        {
            attempts = ++_failedAttempts;
            if (attempts >= options.Value.MaxFailedLoginAttempts)
            {
                _lockedUntil = clock.GetUtcNow().AddSeconds(options.Value.LoginLockoutSeconds);
                _failedAttempts = 0;
                lockedOut = true;
            }
        }

        // Never log the PIN itself.
        logger.LogWarning("Failed login attempt {Attempt}: {Reason}", attempts, reason);
        if (lockedOut)
        {
            logger.LogWarning("Login locked for {Seconds}s after repeated failures", options.Value.LoginLockoutSeconds);
            return LoginResult.Fail($"Too many attempts. Try again in {options.Value.LoginLockoutSeconds} seconds.");
        }
        return LoginResult.Fail("Incorrect PIN.");
    }
}
