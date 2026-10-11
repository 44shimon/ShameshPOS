using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

public class DiscountCalculatorTests
{
    private static SaleLineInput L(decimal price, int qty, Discount? discount = null, decimal tax = 0m) => new(1, "P", price, tax, qty, discount);

    [Fact]
    public void Sale_discount_is_spread_to_the_cent_with_largest_remainders()
    {
        Assert.Equal([0.64m, 0.36m], SaleCalculator.Allocate(1.00m, [5.40m, 3.00m]));
        Assert.Equal([0.04m, 0.03m, 0.03m], SaleCalculator.Allocate(0.10m, [1m, 1m, 1m]));
        Assert.Equal([0m, 0m], SaleCalculator.Allocate(0m, [1m, 2m]));
        // Never more than a line is worth, even when the discount is the whole sale.
        Assert.Equal([0.01m, 9.99m], SaleCalculator.Allocate(10.00m, [0.01m, 9.99m]));
    }

    [Fact]
    public void Discounts_never_exceed_the_price()
    {
        Assert.Equal(5.00m, Discount.Percent(100m).AmountOff(5.00m));
        Assert.Equal(5.00m, Discount.Amount(10m).AmountOff(5.00m));
        Assert.Equal(0.33m, Discount.Percent(33.33m).AmountOff(1.00m));
        Assert.NotNull(Discount.Percent(101m).Problem);
        Assert.NotNull(Discount.Amount(0.001m).Problem);
        Assert.Null(Discount.Amount(1.25m).Problem);
    }

    [Fact]
    public void Line_and_sale_discounts_come_off_before_tax()
    {
        // Coke 3 × 2.00 with 10% off (5.40), chips 2 × 1.50 (3.00); then $1.00 off the sale → 0.64 / 0.36.
        var b = SaleCalculator.Calculate([L(2.00m, 3, Discount.Percent(10m), 0.08875m), L(1.50m, 2, null, 0.08875m)], Discount.Amount(1m));

        Assert.Equal([(6.00m, 0.60m, 0.64m, 4.76m, 0.42m), (3.00m, 0m, 0.36m, 2.64m, 0.23m)],
            b.Lines.Select(l => (l.Gross, l.LineDiscount, l.SaleDiscountShare, l.Net, l.Tax)));
        Assert.Equal((7.40m, 0.65m, 8.05m, 1.60m), (b.Subtotal, b.Tax, b.Total, b.Discount));
    }

    [Fact]
    public void Cart_shows_discounted_lines_and_totals()
    {
        var cart = new ViewModels.CartViewModel();
        var line = cart.Add(new Product { Id = 1, Name = "Coke", Price = 2.00m }, 3);
        cart.Add(new Product { Id = 2, Name = "Chips", Price = 1.50m }, 2);

        cart.SetDiscount(line, Discount.Percent(10m));
        cart.SaleDiscount = Discount.Amount(1m);

        Assert.Equal(5.40m, line.LineTotal);
        Assert.Equal(1.00m, cart.SaleDiscountAmount);
        Assert.Equal(1.60m, cart.DiscountTotal);
        Assert.Equal(7.40m, cart.Total);
        Assert.Equal(Discount.Percent(10m), cart.ToSaleLines()[0].Discount);

        cart.SetQuantity(line, 1); // 10% follows the quantity
        Assert.Equal(1.80m, line.LineTotal);

        cart.Clear();
        Assert.Null(cart.SaleDiscount);
    }
}

[Collection(PostgresTestBase.CollectionName)]
public class Phase7Tests : PostgresTestBase
{
    private readonly IOptions<PosOptions> _pos = Options.Create(new PosOptions { TerminalName = "TEST-1" });

    public Phase7Tests()
    {
        // Receipts are checked as printed by the app, which uses the culture without the PC's own overrides
        // (Windows can be set to show negative money as "($1.00)").
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    private SaleService Sales(string terminal = "TEST-1") =>
        new(DbFactory, Session, new ReceiptNumberGenerator(), Options.Create(new PosOptions { TerminalName = terminal }), TimeProvider.System,
            NullLogger<SaleService>.Instance);
    private RefundService Refunds() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<RefundService>.Instance);
    private ReportService Reports() => new(DbFactory, Session, TimeProvider.System);
    private ShiftService Shifts() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<ShiftService>.Instance);
    private StockService Stock() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<StockService>.Instance);
    private CustomerService Customers() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<CustomerService>.Instance);
    private HeldSaleService Held() => new(DbFactory, Session, _pos, TimeProvider.System, NullLogger<HeldSaleService>.Instance);
    private CatalogAdminService Catalog() => new(DbFactory, Session, TimeProvider.System, NullLogger<CatalogAdminService>.Instance);

    private async Task<(Product Coke, Product Chips, PaymentMethod Cash, PaymentMethod Card, PaymentMethod Account)> FixturesAsync()
    {
        await using var db = DbFactory.CreateDbContext();
        return (await db.Products.SingleAsync(p => p.Name == "Coke"), await db.Products.SingleAsync(p => p.Name == "Chips"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "CASH"), await db.PaymentMethods.SingleAsync(m => m.Code == "CREDIT"),
            await db.PaymentMethods.SingleAsync(m => m.Code == "ACCOUNT"));
    }

    private static SaleLineInput L(Product p, int qty, Discount? discount = null, decimal? tax = null) =>
        new(p.Id, p.Name, p.Price, tax ?? p.TaxRate, qty, discount);

    private void SignInAsCashier() =>
        Session.SignIn(new User { Id = Session.CurrentUser!.Id, Username = "c", DisplayName = "Cashier", PinHash = "x", Role = UserRole.Cashier });

    // ---- Split payments ----------------------------------------------------------------------

    [PostgresFact]
    public async Task Sale_can_be_paid_part_cash_part_card()
    {
        var (coke, chips, cash, card, _) = await FixturesAsync();
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2), L(chips, 1)],   // 5.50
            [new PaymentInput(cash.Id, null, 3.00m), new PaymentInput(card.Id, null)]));

        Assert.Equal("Cash + Credit Card", sale.PaymentMethodName);
        Assert.Equal(0m, sale.ChangeGiven);
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal([(cash.Id, 3.00m), (card.Id, 2.50m)],
            (await db.Payments.Where(p => p.SaleId == sale.SaleId).OrderBy(p => p.Id).ToListAsync()).Select(p => (p.PaymentMethodId, p.Amount)));

        var s = await Reports().GetSummaryAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.Equal([("Cash", 3.00m), ("Credit Card", 2.50m)], s.ByPaymentMethod.Select(m => (m.Name, m.Amount)));
        Assert.Equal(3.00m, s.ExpectedCash);
        var receipt = string.Join("\n", ReceiptFormatter.Receipt(await Reports().GetReceiptAsync(sale.SaleId), new ReceiptSettings([], [])));
        Assert.Matches(@"Cash\s+\$3\.00", receipt);
        Assert.Matches(@"Credit Card\s+\$2\.50", receipt);
    }

    [PostgresFact]
    public async Task Change_comes_from_the_cash_that_settles_the_sale()
    {
        var (coke, chips, cash, card, _) = await FixturesAsync();
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2), L(chips, 1)],   // 5.50
            [new PaymentInput(card.Id, 2.00m), new PaymentInput(cash.Id, null, 5.00m)]));

        Assert.Equal(1.50m, sale.ChangeGiven);
        await using var db = DbFactory.CreateDbContext();
        var cashPayment = await db.Payments.SingleAsync(p => p.SaleId == sale.SaleId && p.PaymentMethodId == cash.Id);
        Assert.Equal((3.50m, 5.00m, 1.50m), (cashPayment.Amount, cashPayment.CashReceived!.Value, cashPayment.ChangeGiven!.Value));
    }

    [PostgresFact]
    public async Task Payments_must_cover_the_total_exactly_or_nothing_is_saved()
    {
        var (coke, _, cash, card, _) = await FixturesAsync();
        var lines = new[] { L(coke, 2) }; // 4.00

        var tooMuch = await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(new CompleteSaleRequest(lines, [new PaymentInput(card.Id, 6.00m)])));
        Assert.Contains("more than", tooMuch.Message);
        var tooLittle = await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(new CompleteSaleRequest(lines, [new PaymentInput(card.Id, 1.00m)])));
        Assert.Contains("still due", tooLittle.Message);
        // Change only on the payment that settles the sale.
        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(new CompleteSaleRequest(lines,
            [new PaymentInput(cash.Id, 1.00m, 5.00m), new PaymentInput(card.Id, null)])));

        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(0, await db.Sales.CountAsync());
    }

    [PostgresFact]
    public async Task Part_of_a_sale_can_be_charged_to_an_account_within_the_limit()
    {
        var (coke, chips, cash, _, account) = await FixturesAsync();
        var noa = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Noa", null, null, null, 3.00m, true));
        var lines = new[] { L(coke, 2), L(chips, 1) }; // 5.50

        await Assert.ThrowsAsync<PosException>(() => Sales().CompleteSaleAsync(new CompleteSaleRequest(lines,
            [new PaymentInput(account.Id, 4.00m), new PaymentInput(cash.Id, null, 1.50m)], noa.Id)));

        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest(lines,
            [new PaymentInput(account.Id, 3.00m), new PaymentInput(cash.Id, null, 2.50m)], noa.Id));
        Assert.Equal(3.00m, sale.CustomerBalance);
        await using var db = DbFactory.CreateDbContext();
        Assert.Equal(3.00m, (await db.CustomerTransactions.SingleAsync()).Amount);
    }

    // ---- Discounts -----------------------------------------------------------------------------

    [PostgresFact]
    public async Task Discounts_are_saved_per_line_printed_reported_and_refunded_at_the_discounted_price()
    {
        var (coke, chips, cash, _, _) = await FixturesAsync();
        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest(
            [L(coke, 3, Discount.Percent(10m), 0.08875m), L(chips, 2, null, 0.08875m)], cash.Id, 10m, null, Discount.Amount(1m)));

        Assert.Equal((8.05m, 1.60m), (sale.Total, sale.Discount));
        await using (var db = DbFactory.CreateDbContext())
        {
            var saved = await db.Sales.Include(s => s.Items).SingleAsync(s => s.Id == sale.SaleId);
            Assert.Equal((7.40m, 0.65m, 1.60m), (saved.Subtotal, saved.Tax, saved.Discount));
            Assert.Equal([(0.60m, 0.64m, 4.76m), (0m, 0.36m, 2.64m)],
                saved.Items.OrderBy(i => i.Id).Select(i => (i.Discount, i.SaleDiscountShare, i.LineTotal)));
        }

        var receipt = string.Join("\n", ReceiptFormatter.Receipt(await Reports().GetReceiptAsync(sale.SaleId), new ReceiptSettings([], [])));
        Assert.Matches(@"  Discount\s+-\$0\.60", receipt);
        Assert.Matches(@"Discount on sale\s+-\$1\.00", receipt);
        Assert.Matches(@"TOTAL\s+\$8\.05", receipt);

        var summary = await Reports().GetSummaryAsync(DateTime.Today, DateTime.Today.AddDays(1));
        Assert.Equal((1.60m, 9.00m), (summary.Discounts, summary.GrossSales));

        // One of the three discounted cokes comes back at a third of what the line cost: 4.76 / 3 → 1.59 + tax 0.14.
        var found = await Refunds().FindSaleAsync(sale.ReceiptNumber);
        var refund = await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], cash.Id, null));
        Assert.Equal(1.73m, refund.Total);
        var all = await Refunds().RefundAsync(new RefundRequest(found.SaleId,
            [new(found.Lines[0].SaleItemId, 2), new(found.Lines[1].SaleItemId, 2)], cash.Id, null));
        Assert.Equal(sale.Total, refund.Total + all.Total);
    }

    [PostgresFact]
    public async Task Only_administrators_can_give_discounts()
    {
        var (coke, _, cash, _, _) = await FixturesAsync();
        SignInAsCashier();

        var ex = await Assert.ThrowsAsync<PosException>(() =>
            Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1, Discount.Amount(0.50m))], cash.Id, 5m)));
        Assert.Contains("administrator", ex.Message);
        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1)], cash.Id, 5m));
    }

    [PostgresFact]
    public async Task Held_sales_keep_their_discounts()
    {
        var (coke, chips, _, _, _) = await FixturesAsync();
        var id = await Held().HoldAsync([L(coke, 2, Discount.Percent(50m)), L(chips, 1)], null, "Staff", Discount.Amount(0.50m));

        Assert.Equal(3.00m, Assert.Single(await Held().ListAsync()).Total); // 2.00 + 1.50 − 0.50
        var content = await Held().RetrieveAsync(id);
        Assert.Equal([Discount.Percent(50m), null], content.Lines.Select(l => l.Discount));
        Assert.Equal(Discount.Amount(0.50m), content.SaleDiscount);
    }

    // ---- Stock ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Stock_follows_sales_returns_deliveries_and_counts()
    {
        var (coke, chips, cash, _, _) = await FixturesAsync();
        await Catalog().SaveProductAsync(new ProductInput(coke.Id, coke.Name, coke.CategoryId, coke.Code, coke.Sku, coke.Barcode, coke.Price,
            coke.TaxRate, coke.SortOrder, true, coke.ShowOnMainScreen, TrackStock: true, ReorderLevel: 5));
        Assert.Equal(10, await Stock().CountAsync(coke.Id, 10, "Opening count"));

        var sale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 3), L(chips, 1)], cash.Id, 10m));
        Assert.Equal(7, await QuantityAsync(coke.Id));

        var found = await Refunds().FindSaleAsync(sale.ReceiptNumber);
        await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], cash.Id, null));
        Assert.Equal(8, await QuantityAsync(coke.Id));
        await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], cash.Id, "Opened", Restock: false));
        Assert.Equal(8, await QuantityAsync(coke.Id));

        Assert.Equal(20, await Stock().ReceiveAsync(coke.Id, 12, "Delivery"));
        Assert.Equal(18, await Stock().CountAsync(coke.Id, 18, null));

        var history = await Stock().GetMovementsAsync(coke.Id);
        Assert.Equal([StockMovementType.Count, StockMovementType.Received, StockMovementType.Refund, StockMovementType.Sale, StockMovementType.Count],
            history.Select(m => m.Type));
        Assert.Equal(18, history.Sum(m => m.Change));
        Assert.Equal(sale.SaleId, history.Single(m => m.Type == StockMovementType.Sale).SaleId);

        await using var db = DbFactory.CreateDbContext();
        Assert.False(await db.StockMovements.AnyAsync(m => m.ProductId == chips.Id)); // not tracked
        await Assert.ThrowsAsync<PosException>(() => Stock().ReceiveAsync(chips.Id, 5, null));
    }

    [PostgresFact]
    public async Task Selling_more_than_is_counted_goes_below_zero_and_shows_low_stock()
    {
        var (coke, _, cash, _, _) = await FixturesAsync();
        await Catalog().SaveProductAsync(new ProductInput(coke.Id, coke.Name, coke.CategoryId, coke.Code, coke.Sku, coke.Barcode, coke.Price,
            coke.TaxRate, coke.SortOrder, true, coke.ShowOnMainScreen, TrackStock: true, ReorderLevel: 2));
        await Stock().CountAsync(coke.Id, 1, null);

        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2)], cash.Id, 5m));

        var product = (await Catalog().GetProductsAsync()).Single(p => p.Id == coke.Id);
        Assert.Equal(-1, product.StockQuantity);
        Assert.True(product.IsLowOnStock);
    }

    [PostgresFact]
    public async Task Cashiers_cannot_change_stock()
    {
        var (coke, _, _, _, _) = await FixturesAsync();
        SignInAsCashier();
        await Assert.ThrowsAsync<PosException>(() => Stock().ReceiveAsync(coke.Id, 5, null));
    }

    private async Task<int> QuantityAsync(int productId)
    {
        await using var db = DbFactory.CreateDbContext();
        return (await db.Products.SingleAsync(p => p.Id == productId)).StockQuantity;
    }

    // ---- Shifts --------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Closing_a_shift_compares_counted_cash_with_float_plus_cash_taken()
    {
        var (coke, chips, cash, card, _) = await FixturesAsync();
        SignInAsCashier(); // cashiers run their own drawer
        Assert.Null(await Shifts().GetOpenShiftAsync());
        var opened = await Shifts().OpenAsync(100.00m);
        Assert.Equal(100.00m, (await Shifts().GetOpenShiftAsync())!.OpeningFloat);
        var again = await Assert.ThrowsAsync<PosException>(() => Shifts().OpenAsync(50m));
        Assert.Contains("already open", again.Message);

        var cashSale = await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 2), L(chips, 1)], cash.Id, 10m)); // 5.50 in, 4.50 change
        await Sales().CompleteSaleAsync(new CompleteSaleRequest([L(coke, 1)], card.Id, null));                         // card: not in the drawer
        await Sales("TEST-2").CompleteSaleAsync(new CompleteSaleRequest([L(coke, 5)], cash.Id, 10m));                  // another register

        Session.SignIn(new User { Id = Session.CurrentUser!.Id, Username = "a", DisplayName = "Admin", PinHash = "x", Role = UserRole.Administrator });
        var noa = await Customers().SaveCustomerAsync(new CustomerInput(0, null, "Noa", null, null, null, 100m, true));
        await Customers().ReceivePaymentAsync(noa.Id, 1.00m, cash.Id);                                                 // 1.00 cash on account
        var found = await Refunds().FindSaleAsync(cashSale.ReceiptNumber);
        await Refunds().RefundAsync(new RefundRequest(found.SaleId, [new(found.Lines[0].SaleItemId, 1)], cash.Id, null)); // 2.00 cash out

        var report = await Shifts().CloseAsync(104.00m, "  Counted twice ");

        Assert.Equal((2, 7.50m, 5.50m, 1.00m, 2.00m), (report.SaleCount, report.SalesTotal, report.CashSales, report.CashAccountPayments, report.CashRefunds));
        Assert.Equal((104.50m, 104.00m, -0.50m), (report.ExpectedCash, report.CountedCash!.Value, report.Difference!.Value));
        Assert.Equal("Counted twice", report.Note);
        Assert.Null(await Shifts().GetOpenShiftAsync());
        var none = await Assert.ThrowsAsync<PosException>(() => Shifts().CloseAsync(0m, null));
        Assert.Contains("No shift", none.Message);

        var listed = Assert.Single(await Shifts().ListAsync(DateTime.Today, DateTime.Today.AddDays(1)));
        Assert.Equal((opened.Id, -0.50m), (listed.Id, listed.Difference!.Value));
        var reprint = await Shifts().GetReportAsync(opened.Id);
        Assert.Equal(104.50m, reprint.ExpectedCash);

        var text = string.Join("\n", ReceiptFormatter.Shift(report, "Shmesh", "Admin", DateTime.Now));
        Assert.Matches(@"Opening float\s+\$100\.00", text);
        Assert.Matches(@"Expected cash\s+\$104\.50", text);
        Assert.Matches(@"SHORT\s+-\$0\.50", text);

        // A new shift can start once the last one is closed.
        await Shifts().OpenAsync(0m);
    }

    [PostgresFact]
    public async Task Shift_amounts_are_validated()
    {
        await Assert.ThrowsAsync<PosException>(() => Shifts().OpenAsync(-1m));
        await Assert.ThrowsAsync<PosException>(() => Shifts().OpenAsync(1.005m));
        await Shifts().OpenAsync(50m);
        await Assert.ThrowsAsync<PosException>(() => Shifts().CloseAsync(-5m, null));
        await Assert.ThrowsAsync<PosException>(() => Shifts().CloseAsync(5m, new string('x', 201)));
    }
}
