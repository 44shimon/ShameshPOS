using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Data;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

public interface ISettingsService
{
    /// <summary>Most a customer may owe on their account unless they have their own limit.</summary>
    Task<decimal> GetDefaultCreditLimitAsync(CancellationToken cancellationToken = default);

    Task SaveDefaultCreditLimitAsync(decimal limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store-wide settings shared by all registers. Values are read from the database each time (not cached)
/// so a change made on one register applies to the others immediately.
/// </summary>
public class SettingsService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    TimeProvider clock,
    ILogger<SettingsService> logger) : ISettingsService
{
    public const string DefaultCreditLimitKey = "customers.default_credit_limit";
    public const decimal FallbackDefaultCreditLimit = 50.00m;
    public const decimal MaxCreditLimit = 99_999_999.99m;

    public async Task<decimal> GetDefaultCreditLimitAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadDefaultCreditLimitAsync(db, cancellationToken);
    }

    /// <summary>Reads the default limit using an existing context, e.g. inside a sale transaction.</summary>
    public static async Task<decimal> ReadDefaultCreditLimitAsync(PosDbContext db, CancellationToken cancellationToken)
    {
        var value = await db.StoreSettings.AsNoTracking()
            .Where(s => s.Key == DefaultCreditLimitKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var limit)
            ? limit
            : FallbackDefaultCreditLimit;
    }

    public async Task SaveDefaultCreditLimitAsync(decimal limit, CancellationToken cancellationToken = default)
    {
        if (session.CurrentUser is not { } admin || !session.HasPermission(Permission.ManageSettings))
            throw new PosException("You are not allowed to change settings.");
        if (limit is < 0 or > MaxCreditLimit || decimal.Round(limit, 2) != limit)
            throw new PosException("Enter a credit limit between $0.00 and $99,999,999.99.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.StoreSettings.FirstOrDefaultAsync(s => s.Key == DefaultCreditLimitKey, cancellationToken);
        var before = setting?.Value;
        if (setting is null)
        {
            setting = new StoreSetting { Key = DefaultCreditLimitKey, Value = "" };
            db.StoreSettings.Add(setting);
        }
        setting.Value = limit.ToString("0.00", CultureInfo.InvariantCulture);
        setting.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Admin {AdminId} changed default customer credit limit {Before} → {After}", admin.Id, before ?? "unset", setting.Value);
    }
}
