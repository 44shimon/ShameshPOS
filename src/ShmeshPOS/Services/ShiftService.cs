using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <summary>The shift open on this register.</summary>
public sealed record ShiftInfo(int Id, string Terminal, string OpenedBy, DateTime OpenedAt, decimal OpeningFloat);

/// <summary>A shift's cash and sales, as printed when it closes (or so far, while it is open). Times are local.</summary>
public sealed record ShiftReport(
    int ShiftId,
    string Terminal,
    string OpenedBy,
    DateTime OpenedAt,
    string? ClosedBy,
    DateTime? ClosedAt,
    decimal OpeningFloat,
    int SaleCount,
    decimal SalesTotal,
    IReadOnlyList<NamedTotal> ByPaymentMethod,
    decimal CashSales,
    decimal CashAccountPayments,
    decimal CashRefunds,
    decimal ExpectedCash,
    decimal? CountedCash,
    string? Note)
{
    public bool IsOpen => ClosedAt is null;

    /// <summary>Counted less expected: positive when the drawer is over, negative when it is short.</summary>
    public decimal? Difference => CountedCash - ExpectedCash;
}

public sealed record ShiftListItem(
    int Id, string Terminal, string OpenedBy, DateTime OpenedAt, string? ClosedBy, DateTime? ClosedAt,
    decimal OpeningFloat, decimal? ExpectedCash, decimal? CountedCash)
{
    public bool IsOpen => ClosedAt is null;
    public decimal? Difference => CountedCash - ExpectedCash;
}

public interface IShiftService
{
    /// <summary>The shift open on this register, or null when none is.</summary>
    Task<ShiftInfo?> GetOpenShiftAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a shift on this register with the float counted into the drawer.</summary>
    Task<ShiftInfo> OpenAsync(decimal openingFloat, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes this register's shift with the cash counted in the drawer. The expected cash is worked out and
    /// saved at the same moment, so the report never changes afterwards.
    /// </summary>
    Task<ShiftReport> CloseAsync(decimal countedCash, string? note, CancellationToken cancellationToken = default);

    /// <summary>A shift's report, for reprinting (figures so far when it is still open).</summary>
    Task<ShiftReport> GetReportAsync(int shiftId, CancellationToken cancellationToken = default);

    /// <summary>Shifts opened from <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive), local time, newest first.</summary>
    Task<IReadOnlyList<ShiftListItem>> ListAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
}

/// <summary>
/// Shifts per register. A shift's cash is everything taken or paid out in cash on that register while it was
/// open: cash sales (net of change), cash paid onto customer accounts, and cash refunds.
/// </summary>
public class ShiftService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<ShiftService> logger) : IShiftService
{
    public const decimal MaxCash = 999_999.99m;
    public const int MaxNoteLength = 200;

    private string Terminal => options.Value.TerminalName;

    public async Task<ShiftInfo?> GetOpenShiftAsync(CancellationToken cancellationToken = default)
    {
        if (!session.IsSignedIn) throw new PosException("Please log in.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var shift = await db.Shifts.AsNoTracking().Include(s => s.OpenedBy)
            .FirstOrDefaultAsync(s => s.TerminalName == Terminal && s.ClosedAt == null, cancellationToken);
        return shift is null ? null : ToInfo(shift);
    }

    public async Task<ShiftInfo> OpenAsync(decimal openingFloat, CancellationToken cancellationToken = default)
    {
        var user = Require(Permission.RunShifts, "open shifts");
        CheckAmount(openingFloat, "opening float");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var shift = new Shift
        {
            TerminalName = Terminal,
            OpenedById = user.Id,
            OpenedAt = clock.GetUtcNow().UtcDateTime,
            OpeningFloat = openingFloat,
        };
        db.Shifts.Add(shift);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PosException("A shift is already open on this register.", ex);
        }

        logger.LogInformation("Shift {ShiftId} opened on {Terminal} by user {UserId} with float {Float}", shift.Id, Terminal, user.Id, openingFloat);
        shift.OpenedBy = user;
        return ToInfo(shift);
    }

    public async Task<ShiftReport> CloseAsync(decimal countedCash, string? note, CancellationToken cancellationToken = default)
    {
        var user = Require(Permission.RunShifts, "close shifts");
        CheckAmount(countedCash, "cash counted");
        var cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote?.Length > MaxNoteLength) throw new PosException($"Keep the note to {MaxNoteLength} characters or fewer.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Locked so a second close (e.g. a double tap) waits and then finds the shift already closed.
        var shift = (await db.Shifts
                .FromSqlInterpolated($"SELECT * FROM shifts WHERE terminal_name = {Terminal} AND closed_at IS NULL FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault()
            ?? throw new PosException("No shift is open on this register.");

        var now = clock.GetUtcNow().UtcDateTime;
        var cash = await CashAsync(db, shift.TerminalName, shift.OpenedAt, now, cancellationToken);
        shift.ClosedById = user.Id;
        shift.ClosedAt = now;
        shift.ExpectedCash = shift.OpeningFloat + cash.In;
        shift.CountedCash = countedCash;
        shift.Note = cleanNote;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Shift {ShiftId} closed on {Terminal} by user {UserId}: expected {Expected}, counted {Counted}, difference {Difference}",
            shift.Id, Terminal, user.Id, shift.ExpectedCash, countedCash, shift.Difference);
        return await BuildReportAsync(db, shift.Id, cancellationToken);
    }

    public async Task<ShiftReport> GetReportAsync(int shiftId, CancellationToken cancellationToken = default)
    {
        Require(Permission.ViewReports, "view shift reports");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildReportAsync(db, shiftId, cancellationToken);
    }

    public async Task<IReadOnlyList<ShiftListItem>> ListAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        Require(Permission.ViewReports, "view shift reports");
        if (to <= from) throw new PosException("The end of the period must be after the start.");
        var zone = clock.LocalTimeZone;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from, DateTimeKind.Unspecified), zone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(to, DateTimeKind.Unspecified), zone);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Shifts.AsNoTracking()
            .Where(s => s.OpenedAt >= fromUtc && s.OpenedAt < toUtc)
            .OrderByDescending(s => s.OpenedAt)
            .Select(s => new
            {
                s.Id, s.TerminalName, OpenedBy = s.OpenedBy!.DisplayName, s.OpenedAt, ClosedBy = s.ClosedBy!.DisplayName, s.ClosedAt,
                s.OpeningFloat, s.ExpectedCash, s.CountedCash,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ShiftListItem(r.Id, r.TerminalName, r.OpenedBy, ToLocal(r.OpenedAt), r.ClosedBy,
            r.ClosedAt is { } closed ? ToLocal(closed) : null, r.OpeningFloat, r.ExpectedCash, r.CountedCash)).ToList();
    }

    private async Task<ShiftReport> BuildReportAsync(PosDbContext db, int shiftId, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking()
            .Include(s => s.OpenedBy)
            .Include(s => s.ClosedBy)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct)
            ?? throw new PosException("That shift could not be found.");

        var end = shift.ClosedAt ?? clock.GetUtcNow().UtcDateTime;
        var cash = await CashAsync(db, shift.TerminalName, shift.OpenedAt, end, ct);
        var sales = SalesIn(db, shift.TerminalName, shift.OpenedAt, end);
        var count = await sales.CountAsync(ct);
        var total = await sales.SumAsync(s => (decimal?)s.Total, ct) ?? 0m;
        var byMethod = await db.Payments.AsNoTracking()
            .Where(p => sales.Any(s => s.Id == p.SaleId))
            .GroupBy(p => new { p.PaymentMethod!.Name, p.PaymentMethod.SortOrder })
            .Select(g => new { g.Key.Name, g.Key.SortOrder, Count = g.Count(), Amount = g.Sum(p => p.Amount) })
            .OrderBy(x => x.SortOrder)
            .ToListAsync(ct);

        return new ShiftReport(
            shift.Id,
            shift.TerminalName,
            shift.OpenedBy?.DisplayName ?? "",
            ToLocal(shift.OpenedAt),
            shift.ClosedBy?.DisplayName,
            shift.ClosedAt is { } closed ? ToLocal(closed) : null,
            shift.OpeningFloat,
            count,
            total,
            byMethod.Select(m => new NamedTotal(m.Name, m.Count, m.Amount)).ToList(),
            cash.Sales,
            cash.AccountPayments,
            cash.Refunds,
            // A closed shift keeps the figure it was closed with; an open one shows the cash so far.
            shift.ExpectedCash ?? shift.OpeningFloat + cash.In,
            shift.CountedCash,
            shift.Note);
    }

    private sealed record CashMovements(decimal Sales, decimal AccountPayments, decimal Refunds)
    {
        public decimal In => Sales + AccountPayments - Refunds;
    }

    /// <summary>Cash taken and paid out on <paramref name="terminal"/> from <paramref name="fromUtc"/> up to and including <paramref name="toUtc"/>.</summary>
    private static async Task<CashMovements> CashAsync(PosDbContext db, string terminal, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var sales = SalesIn(db, terminal, fromUtc, toUtc);
        var cashSales = await db.Payments.AsNoTracking()
            .Where(p => p.PaymentMethod!.Kind == PaymentKind.Cash && sales.Any(s => s.Id == p.SaleId))
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var accountPayments = -(await db.CustomerTransactions.AsNoTracking()
            .Where(t => t.TerminalName == terminal && t.Type == CustomerTransactionType.Payment
                        && t.PaymentMethod!.Kind == PaymentKind.Cash && t.CreatedAt >= fromUtc && t.CreatedAt <= toUtc)
            .SumAsync(t => (decimal?)t.Amount, ct) ?? 0m);

        var refunds = await db.Refunds.AsNoTracking()
            .Where(r => r.TerminalName == terminal && r.PaymentMethod!.Kind == PaymentKind.Cash && r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc)
            .SumAsync(r => (decimal?)r.Total, ct) ?? 0m;

        return new CashMovements(cashSales, accountPayments, refunds);
    }

    private static IQueryable<Sale> SalesIn(PosDbContext db, string terminal, DateTime fromUtc, DateTime toUtc) =>
        db.Sales.AsNoTracking().Where(s => s.TerminalName == terminal && s.Status == SaleStatus.Completed
                                           && s.CreatedAt >= fromUtc && s.CreatedAt <= toUtc);

    private ShiftInfo ToInfo(Shift s) => new(s.Id, s.TerminalName, s.OpenedBy?.DisplayName ?? "", ToLocal(s.OpenedAt), s.OpeningFloat);

    private DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone);

    private static void CheckAmount(decimal amount, string what)
    {
        if (amount is < 0 or > MaxCash || decimal.Round(amount, 2) != amount)
            throw new PosException($"Enter the {what} as an amount, e.g. 100.00.");
    }

    private User Require(Permission permission, string action)
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(permission))
            throw new PosException($"You are not allowed to {action}.");
        return user;
    }
}
