using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

public class ReceiptFormatterTests
{
    private static ReceiptData Sample(decimal? balance = null) => new(
        "2026-000042", new DateTime(2026, 9, 25, 14, 5, 0), "Dana", "REG-1", balance is null ? null : "Noa Friedman", "1002", balance,
        [
            new ReceiptLine("Coke", 1, 2.50m, 2.50m),
            new ReceiptLine("A very long product name that will not fit on one receipt line", 3, 1.25m, 3.75m),
        ],
        6.25m, 0.55m, 6.80m,
        [new ReceiptPayment("Cash", 6.80m, 10.00m, 3.20m)]);

    [Fact]
    public void Every_line_fits_the_paper_width()
    {
        var lines = ReceiptFormatter.Receipt(Sample(12.50m), new ReceiptSettings(["Shmesh Canteen", "123 Main Street, Springfield"], ["Thank you!"]));
        Assert.All(lines, l => Assert.True(l.Length <= ReceiptFormatter.Width, $"Too wide: “{l}”"));
    }

    [Fact]
    public void Receipt_shows_totals_payment_change_and_account_balance()
    {
        var text = string.Join("\n", ReceiptFormatter.Receipt(Sample(12.50m), new ReceiptSettings(["Shmesh"], ["Thanks"])));

        Assert.Contains("Receipt 2026-000042", text);
        Assert.Contains("3 x $1.25", text);
        Assert.Matches(@"TOTAL\s+\$6\.80", text);
        Assert.Matches(@"Change\s+\$3\.20", text);
        Assert.Matches(@"Account balance now\s+\$12\.50", text);
        Assert.Contains("Customer: Noa Friedman #1002", text);
    }

    [Fact]
    public void Values_are_right_aligned()
    {
        var line = ReceiptFormatter.Pair("Subtotal", "$6.25");
        Assert.Equal(ReceiptFormatter.Width, line.Length);
        Assert.EndsWith("$6.25", line);
    }

    [Fact]
    public void Day_report_includes_cash_drawer_and_payments()
    {
        var summary = new SalesSummary(new DateTime(2026, 9, 25), new DateTime(2026, 9, 26), 3, 7, 20m, 1m, 21m,
            [new NamedTotal("Cash", 2, 15m), new NamedTotal("Credit Card", 1, 6m)],
            [new NamedTotal("Dana", 3, 21m)],
            [new ProductSales("Coke", 4, 10m)],
            0m, [new NamedTotal("Cash", 1, 5m)], 15m, 5m);

        var text = string.Join("\n", ReceiptFormatter.Summary(summary, "Shmesh POS", "Admin", new DateTime(2026, 9, 25, 22, 0, 0)));

        Assert.Contains("END OF DAY REPORT", text);
        Assert.Matches(@"TOTAL SALES\s+\$21\.00", text);
        Assert.Matches(@"Expected cash\s+\$20\.00", text);
        Assert.Contains("4 x Coke", text);
        Assert.Equal(7m, summary.AverageSale);
    }
}

[Collection(PostgresTestBase.CollectionName)]
public class ReportIntegrationTests : PostgresTestBase
{
    private readonly IOptions<PosOptions> _pos = Options.Create(new PosOptions { TerminalName = "TEST-1" });

    private SaleService Sales() => new(DbFactory, Session, new ReceiptNumberGenerator(), _pos, TimeProvider.System, NullLogger<SaleService>.Instance);
    private ReportService Reports() => new(DbFactory, Session, TimeProvider.System);

    [PostgresFact]
    public async Task Summary_adds_up_sales_payments_products_and_account_activity()
    {
        await using var db = DbFactory.CreateDbContext();
        var coke = await db.Products.SingleAsync(p => p.Name == "Coke");       // 2.00
        var chips = await db.Products.SingleAsync(p => p.Name == "Chips");     // 1.50
        var cash = await db.PaymentMethods.SingleAsync(m => m.Code == "CASH");
        var credit = await db.PaymentMethods.SingleAsync(m => m.Code == "CREDIT");
        var account = await db.PaymentMethods.SingleAsync(m => m.Code == "ACCOUNT");
        SaleLineInput L(Product p, int q) => new(p.Id, p.Name, p.Price, p.TaxRate, q);

        var customers = new CustomerService(DbFactory, Session, _pos, TimeProvider.System, NullLogger<CustomerService>.Instance);
        var noa = await customers.SaveCustomerAsync(new CustomerInput(0, null, "Noa", null, null, null, 100m, true));

        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2), L(chips, 1)], cash.Id, 10m));   // 5.50 cash
        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1)], credit.Id, null));              // 2.00 card
        var charged = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(chips, 2)], account.Id, null, noa.Id)); // 3.00 account
        await customers.ReceivePaymentAsync(noa.Id, 1.00m, cash.Id);                                           // 1.00 cash on account

        var today = DateTime.Today;
        var s = await Reports().GetSummaryAsync(today, today.AddDays(1));

        Assert.Equal(3, s.SaleCount);
        Assert.Equal(6, s.ItemsSold);
        Assert.Equal(10.50m, s.Total);
        Assert.Equal(3.50m, s.AverageSale);
        Assert.Equal([("Cash", 5.50m), ("Credit Card", 2.00m), ("Charge to Account", 3.00m)],
            s.ByPaymentMethod.Select(m => (m.Name, m.Amount)));
        Assert.Equal(3.00m, s.AccountCharges);
        Assert.Equal(1.00m, s.AccountPaymentsTotal);
        Assert.Equal(6.50m, s.ExpectedCash);
        Assert.Equal([("Coke", 3, 6.00m), ("Chips", 3, 4.50m)], s.TopProducts.Select(p => (p.Name, p.Quantity, p.Revenue)));

        var list = await Reports().GetSalesAsync(today, today.AddDays(1));
        Assert.Equal(3, list.Count);
        Assert.Equal("Noa", list[0].Customer);

        var receipt = await Reports().GetReceiptAsync(charged.SaleId);
        Assert.Equal("Noa", receipt.CustomerName);
        Assert.Equal(3.00m, receipt.CustomerBalanceAfter);

        var yesterday = await Reports().GetSummaryAsync(today.AddDays(-1), today);
        Assert.Equal(0, yesterday.SaleCount);
    }

    [PostgresFact]
    public async Task Cashiers_cannot_see_reports_but_can_reprint_receipts()
    {
        await using var db = DbFactory.CreateDbContext();
        var coke = await db.Products.SingleAsync(p => p.Name == "Coke");
        var cash = await db.PaymentMethods.SingleAsync(m => m.Code == "CASH");
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([new(coke.Id, coke.Name, coke.Price, 0m, 1)], cash.Id, 5m));

        Session.SignIn(new User { Id = Session.CurrentUser!.Id, Username = "c", DisplayName = "C", PinHash = "x", Role = UserRole.Cashier });
        await Assert.ThrowsAsync<PosException>(() => Reports().GetSummaryAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Equal(sale.ReceiptNumber, (await Reports().GetReceiptAsync(sale.SaleId)).ReceiptNumber);
    }

    [PostgresFact]
    public async Task Receipt_and_printer_settings_round_trip()
    {
        var settings = new SettingsService(DbFactory, Session, TimeProvider.System, NullLogger<SettingsService>.Instance);

        Assert.Equal(["Shmesh POS"], (await settings.GetReceiptSettingsAsync()).Header);
        await settings.SaveReceiptSettingsAsync(new ReceiptSettings(["Shmesh Canteen", "Main St", ""], ["Thank you!", "Come again"]));
        var saved = await settings.GetReceiptSettingsAsync();
        Assert.Equal(["Shmesh Canteen", "Main St"], saved.Header);
        Assert.Equal(["Thank you!", "Come again"], saved.Footer);

        await settings.SavePrintSettingsAsync("REG-1", new TerminalPrintSettings("EPSON TM-T20", true));
        Assert.Equal(new TerminalPrintSettings("EPSON TM-T20", true), await settings.GetPrintSettingsAsync("reg-1"));
        Assert.Equal(new TerminalPrintSettings(null, false), await settings.GetPrintSettingsAsync("REG-2"));
    }

    [PostgresFact]
    public async Task Backup_writes_a_restorable_pg_dump_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"shmesh-backup-{Guid.NewGuid():N}");
        await using var db = DbFactory.CreateDbContext();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ShmeshPos"] = db.Database.GetConnectionString(),
        }).Build();
        var backups = new BackupService(config, Options.Create(new BackupOptions { Folder = folder }), Session,
            TimeProvider.System, NullLogger<BackupService>.Instance);
        if (backups.FindPgDump() is null) return; // PostgreSQL client tools not installed on this machine.

        try
        {
            var result = await backups.BackupNowAsync();

            Assert.True(File.Exists(result.Path));
            Assert.True(result.SizeBytes > 1000);
            Assert.Equal("PGDMP", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(result.Path)[..5])); // custom-format header
            Assert.Equal(result.Path, backups.LastBackup()!.Path);
            Assert.Empty(Directory.GetFiles(folder, "*.partial"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
