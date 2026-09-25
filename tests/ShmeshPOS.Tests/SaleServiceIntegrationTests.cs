using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

/// <summary>Runs only when SHMESHPOS_TEST_CONNECTION points at a PostgreSQL server (a throwaway database is created and dropped).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SaleServiceIntegrationTests.ConnectionVariable)))
            Skip = $"Set {SaleServiceIntegrationTests.ConnectionVariable} to run PostgreSQL integration tests.";
    }
}

public class SaleServiceIntegrationTests : IAsyncLifetime
{
    public const string ConnectionVariable = "SHMESHPOS_TEST_CONNECTION";

    private readonly string _connectionString;
    private readonly TestDbFactory _dbFactory;
    private readonly SessionService _session = new();

    public SaleServiceIntegrationTests()
    {
        var baseConnection = Environment.GetEnvironmentVariable(ConnectionVariable) ?? "Host=localhost";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"shmesh_pos_test_{Guid.NewGuid():N}",
            Pooling = false,
        }.ConnectionString;
        _dbFactory = new TestDbFactory(_connectionString);
    }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(ConnectionVariable) is null) return;

        var initializer = new DatabaseInitializer(
            _dbFactory,
            Options.Create(new DatabaseOptions { AutoMigrate = true }),
            Options.Create(new SeedOptions { AdminPin = "2468", SampleData = true }),
            TimeProvider.System,
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var db = _dbFactory.CreateDbContext();
        _session.SignIn(await db.Users.SingleAsync());
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(ConnectionVariable) is null) return;
        await using var db = _dbFactory.CreateDbContext();
        await db.Database.EnsureDeletedAsync();
    }

    private SaleService CreateService() => new(
        _dbFactory,
        _session,
        new ReceiptNumberGenerator(),
        Options.Create(new PosOptions { TerminalName = "TEST-1" }),
        TimeProvider.System,
        NullLogger<SaleService>.Instance);

    private async Task<(Product Product, PaymentMethod Cash, PaymentMethod Credit)> LoadFixturesAsync()
    {
        await using var db = _dbFactory.CreateDbContext();
        var product = await db.Products.OrderBy(p => p.Id).FirstAsync();
        var cash = await db.PaymentMethods.SingleAsync(m => m.Code == "CASH");
        var credit = await db.PaymentMethods.SingleAsync(m => m.Code == "CREDIT");
        return (product, cash, credit);
    }

    [PostgresFact]
    public async Task Cash_sale_writes_sale_items_and_payment_with_sequential_receipts()
    {
        var (product, cash, credit) = await LoadFixturesAsync();
        var service = CreateService();
        var lines = new[] { new SaleLineInput(product.Id, product.Name, product.Price, product.TaxRate, 3) };

        var first = await service.CompleteSaleAsync(new CompleteSaleRequest(lines, cash.Id, 20m));
        var second = await service.CompleteSaleAsync(new CompleteSaleRequest(lines, credit.Id, null));

        var year = DateTime.Now.Year;
        Assert.Equal($"{year}-000001", first.ReceiptNumber);
        Assert.Equal($"{year}-000002", second.ReceiptNumber);
        Assert.Equal(20m - product.Price * 3, first.ChangeGiven);
        Assert.Null(second.ChangeGiven);

        await using var db = _dbFactory.CreateDbContext();
        var sale = await db.Sales.Include(s => s.Items).Include(s => s.Payments).SingleAsync(s => s.Id == first.SaleId);
        Assert.Equal(product.Price * 3, sale.Total);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        var item = Assert.Single(sale.Items);
        Assert.Equal(product.Name, item.ProductName);
        Assert.Equal(3, item.Quantity);
        var payment = Assert.Single(sale.Payments);
        Assert.Equal(20m, payment.CashReceived);
        Assert.Equal(sale.Total, payment.Amount);
    }

    [PostgresFact]
    public async Task Failure_inside_transaction_rolls_back_everything_including_receipt_number()
    {
        var (product, cash, _) = await LoadFixturesAsync();
        var service = CreateService();

        // A product name longer than the column allows makes SaveChanges fail after the receipt number was reserved.
        var badLines = new[] { new SaleLineInput(product.Id, new string('x', 150), product.Price, 0m, 1) };
        await Assert.ThrowsAsync<PosException>(() => service.CompleteSaleAsync(new CompleteSaleRequest(badLines, cash.Id, 100m)));

        await using (var db = _dbFactory.CreateDbContext())
        {
            Assert.Equal(0, await db.Sales.CountAsync());
            Assert.Equal(0, await db.SaleItems.CountAsync());
            Assert.Equal(0, await db.Payments.CountAsync());
            Assert.Equal(0, await db.ReceiptSequences.CountAsync());
        }

        var goodLines = new[] { new SaleLineInput(product.Id, product.Name, product.Price, 0m, 1) };
        var sale = await service.CompleteSaleAsync(new CompleteSaleRequest(goodLines, cash.Id, 100m));
        Assert.EndsWith("-000001", sale.ReceiptNumber);
    }

    [PostgresFact]
    public async Task Insufficient_cash_is_rejected_without_writing()
    {
        var (product, cash, _) = await LoadFixturesAsync();
        var lines = new[] { new SaleLineInput(product.Id, product.Name, product.Price, 0m, 2) };

        var ex = await Assert.ThrowsAsync<PosException>(() =>
            CreateService().CompleteSaleAsync(new CompleteSaleRequest(lines, cash.Id, 0.01m)));

        Assert.Contains("less than", ex.Message);
        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(0, await db.Sales.CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_sales_never_share_a_receipt_number()
    {
        var (product, _, credit) = await LoadFixturesAsync();
        var lines = new[] { new SaleLineInput(product.Id, product.Name, product.Price, 0m, 1) };

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            CreateService().CompleteSaleAsync(new CompleteSaleRequest(lines, credit.Id, null))));

        Assert.Equal(10, results.Select(r => r.ReceiptNumber).Distinct().Count());
    }

    private sealed class TestDbFactory(string connectionString) : IDbContextFactory<PosDbContext>
    {
        public PosDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<PosDbContext>();
            DbConfigurator.Configure(builder, connectionString);
            return new PosDbContext(builder.Options);
        }
    }
}
