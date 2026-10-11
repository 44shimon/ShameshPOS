using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

[Collection(PostgresTestBase.CollectionName)]
public class HeldSaleAndRefundTests : PostgresTestBase
{
    private readonly IOptions<PosOptions> _pos = Options.Create(new PosOptions { TerminalName = "TEST-1" });

    private HeldSaleService Held() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<HeldSaleService>.Instance);
    private RefundService Refunds() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<RefundService>.Instance);
    private SaleService Sales() => new(DbFactory, Session, new ReceiptNumberGenerator(), _pos, TimeProvider.System, NullLogger<SaleService>.Instance);
    private ReportService Reports() => new(DbFactory, Session, TimeProvider.System);

    private async Task<(Product Coke, Product Chips, PaymentMethod Cash, PaymentMethod Card, PaymentMethod Account)> FixturesAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return (await db.Products.SingleAsync(p => p.Name == "Coke"), await db.Products.SingleAsync(p => p.Name == "Chips"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "CASH"), await db.PaymentMethods.SingleAsync(m => m.Code == "CREDIT"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "ACCOUNT"));
    }

    private static SaleLineInput L(Product p, int qty, decimal? tax = null) => new(p.Id, p.Name, p.Price, tax ?? p.TaxRate, qty);

    [PostgresFact]
    public async Task Held_sale_can_be_retrieved_once_with_its_prices_and_customer()
    {
        var (coke, chips, _, _, _) = await FixturesAsync();
        var customers = new CustomerService(DbFactory, Session, _pos, TimeProvider.System, NullLogger<CustomerService>.Instance);
        var noa = await customers.SaveCustomerAsync(new CustomerInput(0, null, "Noa", null, null, null, null, true));

        var id = await Held().HoldAsync([L(coke, 2), L(chips, 1)], noa.Id, "  Table 4 ");
        var list = await Held().ListAsync();
        var summary = Assert.Single(list);
        Assert.Equal(("Table 4", 3, 5.50m, "Noa"), (summary.Label, summary.ItemCount, summary.Total, summary.CustomerName));

        var content = await Held().RetrieveAsync(id);
        Assert.Equal(noa.Id, content.CustomerId);
        Assert.Equal([("Coke", 2, 2.00m), ("Chips", 1, 1.50m)], content.Lines.Select(l => (l.ProductName, l.Quantity, l.UnitPrice)));

        Assert.Empty(await Held().ListAsync());
        var ex = await Assert.ThrowsAsync<PosException>(() => Held().RetrieveAsync(id));
        Assert.Contains("already retrieved", ex.Message);
    }

    [PostgresFact]
    public async Task Partial_then_full_return_refunds_exactly_what_was_paid()
    {
        var (coke, _, cash, _, _) = await FixturesAsync();
        // 3 × $2.00 at 8.875% tax: line 6.00, tax 0.53 (0.5325). Refunding 1 then 2 must give back exactly 6.53.
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 3, 0.08875m)], cash.Id, 10m));
        var found = await Refunds().FindSaleAsync($"  {sale.ReceiptNumber} ");
        var line = Assert.Single(found.Lines);

        var first = await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(line.SaleItemId, 1)], cash.Id, "Flat"));
        Assert.Equal(2.18m, first.Total); // 2.00 + 0.1775 → 0.18
        Assert.EndsWith("-R1", first.RefundNumber);

        var ex = await Assert.ThrowsAsync<PosException>(() =>
            Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(line.SaleItemId, 3)], cash.Id, null)));
        Assert.Contains("Only 2", ex.Message);

        var second = await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(line.SaleItemId, 2)], cash.Id, null));
        Assert.EndsWith("-R2", second.RefundNumber);
        Assert.Equal(sale.Total, first.Total + second.Total);

        var after = await Refunds().FindSaleAsync(sale.ReceiptNumber);
        Assert.Equal(0, after.Lines[0].Remaining);
        Assert.Equal(sale.Total, after.AlreadyRefunded);
    }

    [PostgresFact]
    public async Task Refund_to_account_reduces_what_the_customer_owes()
    {
        var (coke, _, _, _, account) = await FixturesAsync();
        var customers = new CustomerService(DbFactory, Session, _pos, TimeProvider.System, NullLogger<CustomerService>.Instance);
        var noa = await customers.SaveCustomerAsync(new CustomerInput(0, null, "Noa", null, null, null, 100m, true));
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 4)], account.Id, null, noa.Id)); // owes 8.00

        var found = await Refunds().FindSaleAsync(sale.ReceiptNumber);
        var refund = await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], account.Id, null));

        Assert.Equal(6.00m, refund.CustomerBalance);
        await using var db = DbFactory.CreateDbContext();
        var entry = await db.CustomerTransactions.SingleAsync(t => t.Type == CustomerTransactionType.Refund);
        Assert.Equal(-2.00m, entry.Amount);
        Assert.Equal(refund.RefundId, entry.RefundId);
    }

    [PostgresFact]
    public async Task Account_refund_needs_a_customer_on_the_sale()
    {
        var (coke, _, cash, _, account) = await FixturesAsync();
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1)], cash.Id, 5m));
        var found = await Refunds().FindSaleAsync(sale.ReceiptNumber);

        await Assert.ThrowsAsync<PosException>(() =>
            Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], account.Id, null)));
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(0, await db.Refunds.CountAsync());
    }

    [PostgresFact]
    public async Task Reports_show_refunds_net_sales_and_cash_drawer()
    {
        var (coke, chips, cash, card, _) = await FixturesAsync();
        var s1 = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2)], cash.Id, 5m));   // 4.00 cash
        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(chips, 2)], card.Id, null));          // 3.00 card
        var found = await Refunds().FindSaleAsync(s1.ReceiptNumber);
        await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], cash.Id, null)); // -2.00 cash

        var s = await Reports().GetSummaryAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.Equal(7.00m, s.Total);
        Assert.Equal(1, s.RefundCount);
        Assert.Equal(2.00m, s.RefundTotal);
        Assert.Equal(5.00m, s.NetSales);
        Assert.Equal(2.00m, s.ExpectedCash); // 4.00 cash sales − 2.00 cash refund

        var text = string.Join("\n", ReceiptFormatter.Summary(s, "Shmesh", "Admin", DateTime.Now));
        Assert.Matches(@"NET SALES\s+\$5\.00", text);
    }

    [PostgresFact]
    public async Task Cashiers_can_hold_sales_but_not_refund()
    {
        var (coke, _, cash, _, _) = await FixturesAsync();
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1)], cash.Id, 5m));
        Session.SignIn(new User { Id = Session.CurrentUser!.Id, Username = "c", DisplayName = "C", PinHash = "x", Role = UserRole.Cashier });

        await Held().HoldAsync([L(coke, 1)], null, null);
        var ex = await Assert.ThrowsAsync<PosException>(() => Refunds().FindSaleAsync(sale.ReceiptNumber));
        Assert.Contains("administrator", ex.Message);
    }
}
