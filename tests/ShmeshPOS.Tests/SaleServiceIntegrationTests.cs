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
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresTestBase.ConnectionVariable)))
            Skip = $"Set {PostgresTestBase.ConnectionVariable} to run PostgreSQL integration tests.";
        // A stuck test fails with its name instead of stalling the whole run.
        Timeout = 180_000;
    }
}

/// <summary>Creates a throwaway, migrated and seeded database per test class instance, signed in as the seeded admin.</summary>
public abstract class PostgresTestBase : IAsyncLifetime
{
    public const string ConnectionVariable = "SHMESHPOS_TEST_CONNECTION";

    /// <summary>Database tests share one xUnit collection so they never run in parallel: PostgreSQL rejects
    /// concurrent CREATE DATABASE from the same template.</summary>
    public const string CollectionName = "PostgreSQL";
    public const string AdminPin = "2468";

    protected readonly TestDbFactory DbFactory;
    private readonly string _databaseName;
    private readonly string _maintenanceConnectionString;
    protected readonly SessionService Session = new();

    protected PostgresTestBase()
    {
        var baseConnection = Environment.GetEnvironmentVariable(ConnectionVariable) ?? "Host=localhost";
        var builder = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"shmesh_pos_test_{Guid.NewGuid():N}",
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 120,
        };
        _databaseName = builder.Database!;
        _maintenanceConnectionString = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres" }.ConnectionString;
        DbFactory = new TestDbFactory(builder.ConnectionString);
    }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(ConnectionVariable) is null) return;

        var initializer = new DatabaseInitializer(
            DbFactory,
            Options.Create(new DatabaseOptions { AutoMigrate = true }),
            Options.Create(new SeedOptions { AdminPin = AdminPin, SampleData = true }),
            TimeProvider.System,
            NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var db = DbFactory.CreateDbContext();
        Session.SignIn(await db.Users.SingleAsync());
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable(ConnectionVariable) is null) return;
        // Plain DROP DATABASE (not EF's EnsureDeleted, which tries to kill other sessions and is refused for the
        // autovacuum worker that may still be visiting the new database). PostgreSQL cancels autovacuum itself;
        // retry briefly in case it hasn't let go yet.
        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\"", connection);
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 10)
            {
                await Task.Delay(500);
            }
        }
    }

    protected sealed class TestDbFactory(string connectionString) : IDbContextFactory<PosDbContext>
    {
        public PosDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<PosDbContext>();
            DbConfigurator.Configure(builder, connectionString);
            return new PosDbContext(builder.Options);
        }
    }
}

[Collection(PostgresTestBase.CollectionName)]
public class SaleServiceIntegrationTests : PostgresTestBase
{
    private SaleService CreateService() => new(
        DbFactory,
        Session,
        new ReceiptNumberGenerator(),
        Options.Create(new PosOptions { TerminalName = "TEST-1" }),
        TimeProvider.System,
        NullLogger<SaleService>.Instance);

    private async Task<(Product Product, PaymentMethod Cash, PaymentMethod Credit)> LoadFixturesAsync()
    {
        await using var db = DbFactory.CreateDbContext();
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

        await using var db = DbFactory.CreateDbContext();
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

        await using (var db = DbFactory.CreateDbContext())
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
        await using var db = DbFactory.CreateDbContext();
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
}
