using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Data;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <summary>Receipt printing for one register (settings are kept per terminal name).</summary>
/// <param name="DrawerConnected">A cash drawer is plugged into this register's receipt printer.</param>
public sealed record TerminalPrintSettings(string? PrinterName, bool AutoPrint, bool DrawerConnected = false);

public interface ISettingsService
{
    /// <summary>Receipt header/footer text shared by all registers.</summary>
    Task<ReceiptSettings> GetReceiptSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveReceiptSettingsAsync(ReceiptSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Which printer this register prints receipts on, and whether it prints after every sale.</summary>
    Task<TerminalPrintSettings> GetPrintSettingsAsync(string terminal, CancellationToken cancellationToken = default);
    Task SavePrintSettingsAsync(string terminal, TerminalPrintSettings settings, CancellationToken cancellationToken = default);

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
    public const string ReceiptHeaderKey = "receipt.header";
    public const string ReceiptFooterKey = "receipt.footer";
    private const int MaxReceiptLines = 8;
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

    public async Task<ReceiptSettings> GetReceiptSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await ReadAsync(db, [ReceiptHeaderKey, ReceiptFooterKey], cancellationToken);
        return new ReceiptSettings(
            values.TryGetValue(ReceiptHeaderKey, out var header) ? SplitLines(header) : ["Shmesh POS"],
            values.TryGetValue(ReceiptFooterKey, out var footer) ? SplitLines(footer) : ["Thank you!"]);
    }

    public async Task SaveReceiptSettingsAsync(ReceiptSettings settings, CancellationToken cancellationToken = default)
    {
        var admin = RequireSettings();
        var header = CleanLines(settings.Header, "header");
        var footer = CleanLines(settings.Footer, "footer");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await WriteAsync(db, ReceiptHeaderKey, string.Join("\n", header), cancellationToken);
        await WriteAsync(db, ReceiptFooterKey, string.Join("\n", footer), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} changed the receipt header/footer", admin.Id);
    }

    public async Task<TerminalPrintSettings> GetPrintSettingsAsync(string terminal, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (printerKey, autoKey) = PrintKeys(terminal);
        var drawerKey = DrawerKey(terminal);
        var values = await ReadAsync(db, [printerKey, autoKey, drawerKey], cancellationToken);
        return new TerminalPrintSettings(
            values.TryGetValue(printerKey, out var printer) && printer.Length > 0 ? printer : null,
            values.TryGetValue(autoKey, out var auto) && auto == "true",
            values.TryGetValue(drawerKey, out var drawer) && drawer == "true");
    }

    public async Task SavePrintSettingsAsync(string terminal, TerminalPrintSettings settings, CancellationToken cancellationToken = default)
    {
        var admin = RequireSettings();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (printerKey, autoKey) = PrintKeys(terminal);
        await WriteAsync(db, printerKey, settings.PrinterName?.Trim() ?? "", cancellationToken);
        await WriteAsync(db, autoKey, settings.AutoPrint ? "true" : "false", cancellationToken);
        await WriteAsync(db, DrawerKey(terminal), settings.DrawerConnected ? "true" : "false", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Admin {AdminId} set receipt printer for {Terminal} to {Printer} (auto-print {AutoPrint})",
            admin.Id, terminal, settings.PrinterName ?? "Windows default", settings.AutoPrint);
    }

    private static string DrawerKey(string terminal) => $"printer.{terminal.Trim().ToLowerInvariant()}.drawer";

    private static (string Printer, string Auto) PrintKeys(string terminal)
    {
        var t = terminal.Trim().ToLowerInvariant();
        return ($"printer.{t}.name", $"printer.{t}.auto");
    }

    private static async Task<Dictionary<string, string>> ReadAsync(PosDbContext db, string[] keys, CancellationToken ct) =>
        await db.StoreSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, ct);

    private async Task WriteAsync(PosDbContext db, string key, string value, CancellationToken ct)
    {
        var setting = await db.StoreSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            setting = new StoreSetting { Key = key, Value = value };
            db.StoreSettings.Add(setting);
        }
        setting.Value = value;
        setting.UpdatedAt = clock.GetUtcNow().UtcDateTime;
    }

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Length == 0 ? [] : value.Split('\n');

    private static List<string> CleanLines(IReadOnlyList<string> lines, string what)
    {
        var cleaned = lines.Select(l => l.Trim()).ToList();
        while (cleaned.Count > 0 && cleaned[^1].Length == 0) cleaned.RemoveAt(cleaned.Count - 1);
        if (cleaned.Count > MaxReceiptLines) throw new PosException($"The receipt {what} can have at most {MaxReceiptLines} lines.");
        if (cleaned.Any(l => l.Length > 120)) throw new PosException($"Receipt {what} lines must be 120 characters or fewer.");
        return cleaned;
    }

    private User RequireSettings()
    {
        if (session.CurrentUser is not { } admin || !session.HasPermission(Permission.ManageSettings))
            throw new PosException("You are not allowed to change settings.");
        return admin;
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
