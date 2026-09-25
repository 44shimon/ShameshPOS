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

public interface IAuthenticationService
{
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

    public async Task<LoginResult> LoginWithPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        var remainingLockout = GetRemainingLockout();
        if (remainingLockout > TimeSpan.Zero)
        {
            logger.LogWarning("Login rejected: terminal locked out for {Seconds}s", (int)remainingLockout.TotalSeconds);
            return LoginResult.Fail($"Too many attempts. Try again in {(int)Math.Ceiling(remainingLockout.TotalSeconds)} seconds.");
        }

        if (!PinHasher.IsValidPinFormat(pin))
            return RegisterFailure("PIN format invalid");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(cancellationToken);

        // PIN-only login: compare against each active user. PBKDF2 is deliberately slow, so run it off the UI thread.
        var user = await Task.Run(() => candidates.FirstOrDefault(u => PinHasher.Verify(pin, u.PinHash)), cancellationToken);
        if (user is null)
            return RegisterFailure("no matching user");

        lock (_gate)
        {
            _failedAttempts = 0;
            _lockedUntil = null;
        }

        session.SignIn(user);
        logger.LogInformation("Login succeeded for user {UserId} ({Username}, {Role})", user.Id, user.Username, user.Role);
        return LoginResult.Ok(user);
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
