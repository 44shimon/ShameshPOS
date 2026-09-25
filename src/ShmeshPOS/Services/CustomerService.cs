using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <summary>What the register needs to know about a customer's account.</summary>
public sealed record CustomerAccount(
    int Id, string AccountNumber, string Name, string? Phone, decimal Balance, decimal CreditLimit, bool UsesDefaultLimit)
{
    /// <summary>How much more can be charged before the limit is reached.</summary>
    public decimal AvailableCredit => Math.Max(0m, CreditLimit - Balance);
}

public sealed record CustomerInput(
    int Id, string? AccountNumber, string Name, string? Phone, string? Email, string? Notes, decimal? CreditLimit, bool IsActive);

public sealed record AccountPaymentResult(CustomerAccount Account, decimal Amount, string PaymentMethodName);

public interface ICustomerService
{
    /// <summary>Active customers matching a name, account number or phone, for the register.</summary>
    Task<IReadOnlyList<CustomerAccount>> SearchAsync(string text, int max = 20, CancellationToken cancellationToken = default);

    Task<CustomerAccount?> GetAccountAsync(int customerId, CancellationToken cancellationToken = default);

    /// <summary>Records money the customer paid towards their account (cash, card, …).</summary>
    Task<AccountPaymentResult> ReceivePaymentAsync(int customerId, decimal amount, int paymentMethodId, CancellationToken cancellationToken = default);

    // ---- Administration ----
    Task<IReadOnlyList<Customer>> GetCustomersAsync(CancellationToken cancellationToken = default);
    Task<Customer> SaveCustomerAsync(CustomerInput input, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomerTransaction>> GetTransactionsAsync(int customerId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Manual correction. Positive increases what the customer owes, negative decreases it.</summary>
    Task<CustomerAccount> AdjustBalanceAsync(int customerId, decimal amount, string note, CancellationToken cancellationToken = default);
}

public partial class CustomerService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<CustomerService> logger) : ICustomerService
{
    public const int FirstAccountNumber = 1001;

    [GeneratedRegex("^[A-Za-z0-9-]{1,20}$")]
    private static partial Regex AccountNumberPattern();

    public async Task<IReadOnlyList<CustomerAccount>> SearchAsync(string text, int max = 20, CancellationToken cancellationToken = default)
    {
        Require(Permission.UseCustomerAccounts, "use customer accounts");
        var term = text.Trim();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Customers.AsNoTracking().Where(c => c.IsActive);
        if (term.Length > 0)
        {
            var pattern = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(c =>
                c.AccountNumber == term ||
                EF.Functions.ILike(c.Name, pattern) ||
                (c.Phone != null && EF.Functions.ILike(c.Phone, pattern)) ||
                EF.Functions.ILike(c.AccountNumber, pattern));
        }

        var customers = await query
            .OrderBy(c => c.AccountNumber == term ? 0 : 1).ThenBy(c => c.Name)
            .Take(max)
            .ToListAsync(cancellationToken);

        var defaultLimit = await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken);
        return customers.Select(c => ToAccount(c, defaultLimit)).ToList();
    }

    public async Task<CustomerAccount?> GetAccountAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        return customer is null ? null : ToAccount(customer, await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken));
    }

    public async Task<AccountPaymentResult> ReceivePaymentAsync(int customerId, decimal amount, int paymentMethodId, CancellationToken cancellationToken = default)
    {
        var user = Require(Permission.UseCustomerAccounts, "take account payments");
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
            throw new PosException("Enter the amount the customer is paying.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var method = await db.PaymentMethods.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == paymentMethodId && m.IsActive, cancellationToken)
            ?? throw new PosException("That payment method is not available.");
        if (method.Kind == PaymentKind.Account)
            throw new PosException("An account can't be paid by charging an account.");

        var customer = await CustomerLedger.LockAsync(db, customerId, cancellationToken);
        if (!customer.IsActive)
            throw new PosException($"{customer.Name}'s account is closed.");

        var entry = CustomerLedger.Post(customer, CustomerTransactionType.Payment, -amount, user.Id, options.Value.TerminalName, clock.GetUtcNow().UtcDateTime);
        entry.PaymentMethodId = method.Id;
        db.CustomerTransactions.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Account payment of {Amount} by {PaymentMethod} for customer {CustomerId} ({AccountNumber}); balance now {Balance}; user {UserId}",
            amount, method.Code, customer.Id, customer.AccountNumber, customer.Balance, user.Id);

        var defaultLimit = await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken);
        return new AccountPaymentResult(ToAccount(customer, defaultLimit), amount, method.Name);
    }

    public async Task<IReadOnlyList<Customer>> GetCustomersAsync(CancellationToken cancellationToken = default)
    {
        Require(Permission.ManageCustomers, "manage customers");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking()
            .OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Customer> SaveCustomerAsync(CustomerInput input, CancellationToken cancellationToken = default)
    {
        var admin = Require(Permission.ManageCustomers, "manage customers");

        var name = Clean(input.Name) ?? throw new PosException("Enter the customer's name.");
        if (name.Length > 100) throw new PosException("Name must be 100 characters or fewer.");
        var phone = Clean(input.Phone);
        if (phone?.Length > 30) throw new PosException("Phone must be 30 characters or fewer.");
        var email = Clean(input.Email);
        if (email?.Length > 200) throw new PosException("Email must be 200 characters or fewer.");
        var notes = Clean(input.Notes);
        if (notes?.Length > 500) throw new PosException("Notes must be 500 characters or fewer.");
        if (input.CreditLimit is { } limit && (limit < 0 || limit > SettingsService.MaxCreditLimit || decimal.Round(limit, 2) != limit))
            throw new PosException("Enter a credit limit between $0.00 and $99,999,999.99.");

        var accountNumber = Clean(input.AccountNumber);
        if (accountNumber is not null && !AccountNumberPattern().IsMatch(accountNumber))
            throw new PosException("Account number can use letters, numbers and dashes (up to 20).");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        accountNumber ??= await NextAccountNumberAsync(db, cancellationToken);
        var lower = accountNumber.ToLowerInvariant();
        var clash = await db.Customers.AsNoTracking()
            .Where(c => c.Id != input.Id && c.AccountNumber.ToLower() == lower)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);
        if (clash is not null)
            throw new PosException($"Account number {accountNumber} already belongs to {clash}.");

        var now = clock.GetUtcNow().UtcDateTime;
        Customer customer;
        if (input.Id == 0)
        {
            customer = new Customer { AccountNumber = accountNumber, Name = name, CreatedAt = now };
            db.Customers.Add(customer);
        }
        else
        {
            customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == input.Id, cancellationToken)
                ?? throw new PosException("That customer no longer exists.");
        }

        var before = input.Id == 0 ? "new" : Describe(customer);
        customer.AccountNumber = accountNumber;
        customer.Name = name;
        customer.Phone = phone;
        customer.Email = email;
        customer.Notes = notes;
        customer.CreditLimit = input.CreditLimit;
        customer.IsActive = input.IsActive;
        customer.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PosException($"Account number {accountNumber} was just taken. Save again to get a new one.", ex);
        }

        logger.LogInformation("Admin {AdminId} {Action} customer {CustomerId} ({Before} → {After})",
            admin.Id, input.Id == 0 ? "created" : "updated", customer.Id, before, Describe(customer));
        return customer;
    }

    public async Task<IReadOnlyList<CustomerTransaction>> GetTransactionsAsync(int customerId, int take = 50, CancellationToken cancellationToken = default)
    {
        Require(Permission.ManageCustomers, "view account history");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CustomerTransactions.AsNoTracking()
            .Include(t => t.Sale)
            .Include(t => t.PaymentMethod)
            .Include(t => t.User)
            .Where(t => t.CustomerId == customerId)
            .OrderByDescending(t => t.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomerAccount> AdjustBalanceAsync(int customerId, decimal amount, string note, CancellationToken cancellationToken = default)
    {
        var admin = Require(Permission.ManageCustomers, "adjust balances");
        if (amount == 0 || decimal.Round(amount, 2) != amount)
            throw new PosException("Enter the adjustment amount, e.g. 5.00 to add or -5.00 to take off.");
        var reason = Clean(note) ?? throw new PosException("Enter a reason for the adjustment.");
        if (reason.Length > 500) throw new PosException("Reason must be 500 characters or fewer.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var customer = await CustomerLedger.LockAsync(db, customerId, cancellationToken);
        var entry = CustomerLedger.Post(customer, CustomerTransactionType.Adjustment, amount, admin.Id, options.Value.TerminalName, clock.GetUtcNow().UtcDateTime);
        entry.Note = reason;
        db.CustomerTransactions.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Admin {AdminId} adjusted customer {CustomerId} balance by {Amount} ({Reason}); balance now {Balance}",
            admin.Id, customer.Id, amount, reason, customer.Balance);
        return ToAccount(customer, await SettingsService.ReadDefaultCreditLimitAsync(db, cancellationToken));
    }

    public static CustomerAccount ToAccount(Customer c, decimal defaultLimit) =>
        new(c.Id, c.AccountNumber, c.Name, c.Phone, c.Balance, c.CreditLimit ?? defaultLimit, c.CreditLimit is null);

    private static async Task<string> NextAccountNumberAsync(PosDbContext db, CancellationToken ct)
    {
        var numbers = await db.Customers.AsNoTracking().Select(c => c.AccountNumber).ToListAsync(ct);
        var highest = numbers.Select(n => int.TryParse(n, out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return Math.Max(highest + 1, FirstAccountNumber).ToString();
    }

    private User Require(Permission permission, string action)
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(permission))
            throw new PosException($"You are not allowed to {action}.");
        return user;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Describe(Customer c) =>
        $"account={c.AccountNumber}, name={c.Name}, phone={c.Phone}, email={c.Email}, limit={(c.CreditLimit?.ToString("0.00") ?? "default")}, active={c.IsActive}";
}

/// <summary>Balance changes shared by sales, account payments and adjustments.</summary>
public static class CustomerLedger
{
    /// <summary>
    /// Loads the customer with a row lock (SELECT … FOR UPDATE) held until the surrounding transaction ends,
    /// so two registers charging the same account can't both pass the credit-limit check.
    /// </summary>
    public static async Task<Customer> LockAsync(PosDbContext db, int customerId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Customer rows must be locked inside a transaction.");

        var rows = await db.Customers
            .FromSqlInterpolated($"SELECT * FROM customers WHERE id = {customerId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault() ?? throw new PosException("That customer account no longer exists.");
    }

    /// <summary>Applies <paramref name="amount"/> to the balance and returns the matching history entry (not yet added to the context).</summary>
    public static CustomerTransaction Post(Customer customer, CustomerTransactionType type, decimal amount, int userId, string terminal, DateTime now)
    {
        customer.Balance += amount;
        customer.UpdatedAt = now;
        return new CustomerTransaction
        {
            CustomerId = customer.Id,
            Type = type,
            Amount = amount,
            BalanceAfter = customer.Balance,
            UserId = userId,
            TerminalName = terminal,
            CreatedAt = now,
        };
    }
}
