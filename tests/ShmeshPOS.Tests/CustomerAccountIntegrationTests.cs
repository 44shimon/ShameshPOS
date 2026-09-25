using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

[Collection(PostgresTestBase.CollectionName)]
public class CustomerAccountIntegrationTests : PostgresTestBase
{
    private readonly IOptions<PosOptions> _pos = Options.Create(new PosOptions { TerminalName = "TEST-1" });

    private CustomerService Customers() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<CustomerService>.Instance);
    private SettingsService Settings() => new(DbFactory, Session, TimeProvider.System, NullLogger<SettingsService>.Instance);
    private SaleService Sales() => new(DbFactory, Session, new ReceiptNumberGenerator(), _pos, TimeProvider.System, NullLogger<SaleService>.Instance);

    private async Task<(Product Coke, PaymentMethod Account, PaymentMethod Cash)> FixturesAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return (
            await db.Products.SingleAsync(p => p.Name == "Coke"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "ACCOUNT"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "CASH"));
    }

    private static CompleteSaleRequest Charge(Product p, int qty, PaymentMethod account, int customerId) =>
        new([new SaleLineInput(p.Id, p.Name, p.Price, p.TaxRate, qty)], account.Id, null, customerId);

    [PostgresFact]
    public async Task Charge_within_default_limit_updates_balance_and_history()
    {
        var (coke, account, _) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Dana Levi", "555-0100", null, null, null, true));
        Assert.Equal("1001", customer.AccountNumber);

        var sale = await Sales().CompleteSaleAsync(Charge(coke, 3, account, customer.Id));

        Assert.Equal(6.00m, sale.CustomerBalance);
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(6.00m, (await db.Customers.SingleAsync(c => c.Id == customer.Id)).Balance);
        Assert.Equal(customer.Id, (await db.Sales.SingleAsync(s => s.Id == sale.SaleId)).CustomerId);
        var entry = await db.CustomerTransactions.SingleAsync();
        Assert.Equal(CustomerTransactionType.Charge, entry.Type);
        Assert.Equal(6.00m, entry.Amount);
        Assert.Equal(6.00m, entry.BalanceAfter);
        Assert.Equal(sale.SaleId, entry.SaleId);
    }

    [PostgresFact]
    public async Task Charge_over_limit_is_refused_and_nothing_is_saved()
    {
        var (coke, account, _) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, "A-7", "Low Limit", null, null, null, 5.00m, true));

        var ex = await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(Charge(coke, 3, account, customer.Id)));
        Assert.Contains("limit", ex.Message);

        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(0m, (await db.Customers.SingleAsync(c => c.Id == customer.Id)).Balance);
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Equal(0, await db.CustomerTransactions.CountAsync());
        Assert.Equal(0, await db.ReceiptSequences.CountAsync());
    }

    [PostgresFact]
    public async Task Default_limit_setting_applies_to_customers_without_their_own_limit()
    {
        var (coke, account, _) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Uses Default", null, null, null, null, true));

        await Settings().SaveDefaultCreditLimitAsync(3.00m);
        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(Charge(coke, 2, account, customer.Id)));

        await Settings().SaveDefaultCreditLimitAsync(4.00m);
        var sale = await Sales().CompleteSaleAsync(Charge(coke, 2, account, customer.Id));
        Assert.Equal(4.00m, sale.CustomerBalance);

        var info = await Customers().GetAccountAsync(customer.Id);
        Assert.True(info!.UsesDefaultLimit);
        Assert.Equal(0m, info.AvailableCredit);
    }

    [PostgresFact]
    public async Task Zero_limit_blocks_charging_but_cash_sale_can_still_name_the_customer()
    {
        var (coke, account, cash) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Cash Only", null, null, null, 0m, true));

        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(Charge(coke, 1, account, customer.Id)));

        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest(
            [new SaleLineInput(coke.Id, coke.Name, coke.Price, 0m, 1)], cash.Id, 5m, customer.Id));
        Assert.Null(sale.CustomerBalance);
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(customer.Id, (await db.Sales.SingleAsync()).CustomerId);
        Assert.Equal(0m, (await db.Customers.SingleAsync()).Balance);
    }

    [PostgresFact]
    public async Task Charging_requires_an_active_customer()
    {
        var (coke, account, _) = await FixturesAsync();
        var closed = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Closed", null, null, null, 100m, false));

        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(
            new CompleteSaleRequest([new SaleLineInput(coke.Id, coke.Name, coke.Price, 0m, 1)], account.Id, null)));
        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(Charge(coke, 1, account, closed.Id)));
    }

    [PostgresFact]
    public async Task Payment_reduces_balance_and_is_recorded()
    {
        var (coke, account, cash) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Payer", null, null, null, 100m, true));
        await Sales().CompleteSaleAsync(Charge(coke, 5, account, customer.Id));

        var result = await Customers().ReceivePaymentAsync(customer.Id, 7.50m, cash.Id);

        Assert.Equal(2.50m, result.Account.Balance);
        await Assert.ThrowsAsync<PosException>(() => Customers().ReceivePaymentAsync(customer.Id, 1m, account.Id));
        await Assert.ThrowsAsync<PosException>(() => Customers().ReceivePaymentAsync(customer.Id, 0m, cash.Id));

        await using var db = DbFactory.CreateDbContext();
        var payment = await db.CustomerTransactions.SingleAsync(t => t.Type == CustomerTransactionType.Payment);
        Assert.Equal(-7.50m, payment.Amount);
        Assert.Equal(2.50m, payment.BalanceAfter);
        Assert.Equal(cash.Id, payment.PaymentMethodId);
    }

    [PostgresFact]
    public async Task Balance_always_equals_the_sum_of_history()
    {
        var (coke, account, cash) = await FixturesAsync();
        var c = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Ledger", null, null, null, 100m, true));

        await Sales().CompleteSaleAsync(Charge(coke, 4, account, c.Id));
        await Customers().ReceivePaymentAsync(c.Id, 3m, cash.Id);
        await Customers().AdjustBalanceAsync(c.Id, -1.25m, "Spilled drink refund");
        await Assert.ThrowsAsync<PosException>(() => Customers().AdjustBalanceAsync(c.Id, 1m, "  "));

        await using var db = DbFactory.CreateDbContext();
        var balance = (await db.Customers.SingleAsync(x => x.Id == c.Id)).Balance;
        var sum = await db.CustomerTransactions.Where(t => t.CustomerId == c.Id).SumAsync(t => t.Amount);
        Assert.Equal(3.75m, balance);
        Assert.Equal(balance, sum);
    }

    [PostgresFact]
    public async Task Concurrent_charges_never_exceed_the_limit()
    {
        var (coke, account, _) = await FixturesAsync();
        var customer = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Busy", null, null, null, 10.00m, true));

        // Ten registers each try to charge $2.00 at once against a $10.00 limit: exactly five may succeed.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try { await Sales().CompleteSaleAsync(Charge(coke, 1, account, customer.Id)); return true; }
            catch (PosException) { return false; }
        }));

        Assert.Equal(5, attempts.Count(ok => ok));
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(10.00m, (await db.Customers.SingleAsync(c => c.Id == customer.Id)).Balance);
    }

    [PostgresFact]
    public async Task Account_numbers_are_unique_and_cashiers_cannot_manage_customers()
    {
        await Customers().SaveCustomerAsync(new CustomerInput(0, "ABC-1", "First", null, null, null, null, true));
        await Assert.ThrowsAsync<PosException>(() =>
            Customers().SaveCustomerAsync(new CustomerInput(0, "abc-1", "Second", null, null, null, null, true)));

        Session.SignIn(new User { Id = 1, Username = "c", DisplayName = "C", PinHash = "x", Role = UserRole.Cashier });
        await Assert.ThrowsAsync<PosException>(() => Customers().SaveCustomerAsync(new CustomerInput(0, null, "Nope", null, null, null, null, true)));
        await Assert.ThrowsAsync<PosException>(() => Settings().SaveDefaultCreditLimitAsync(1000m));
        Assert.Single(await Customers().SearchAsync("first"));
    }
}
