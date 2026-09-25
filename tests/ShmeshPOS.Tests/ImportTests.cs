using System.IO;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.Tests;

public class ImportValueTests
{
    [Theory]
    [InlineData("8.875%", 0.08875)]
    [InlineData("8.875", 0.08875)]
    [InlineData("0.08875", 0.08875)]
    [InlineData("0", 0)]
    [InlineData("17 %", 0.17)]
    public void Tax_rates_accept_percent_and_fraction(string text, double expected)
    {
        Assert.True(ImportValue.TryTaxRate(text, out var rate));
        Assert.Equal((decimal)expected, rate);
    }

    [Theory]
    [InlineData("$1,234.50", 1234.50)]
    [InlineData(" 2.5 ", 2.50)]
    [InlineData("(3.00)", -3.00)]
    [InlineData("1.999", 2.00)]
    public void Money_is_parsed_leniently(string text, double expected)
    {
        Assert.True(ImportValue.TryMoney(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void Headings_are_matched_by_synonym_ignoring_case_and_punctuation()
    {
        var map = ColumnMap.Build(["Item Description", "UPC", "Retail $", "Dept."],
            new Dictionary<string, string[]>
            {
                ["Name"] = ["Name", "Item Description"],
                ["Barcode"] = ["Barcode", "UPC"],
                ["Price"] = ["Price", "Retail"],
                ["Category"] = ["Category", "Dept"],
            });
        var row = new ImportRow(2, ["Coke", "049000000443", "2.00", "Drinks"]);

        Assert.Equal("Coke", map.Get(row, "Name"));
        Assert.Equal("049000000443", map.Get(row, "Barcode"));
        Assert.Equal("Drinks", map.Get(row, "Category"));
        Assert.Equal("2.00", map.Get(row, "Price")); // "Retail $" normalises to "retail"
    }

    [Theory]
    [InlineData("Item Description", "Name")]
    [InlineData("Product Name (English)", "Name")]
    [InlineData("Selling Price USD", "Price")]
    [InlineData("UPC Barcode", "Barcode")]
    public void Headings_containing_a_known_word_are_matched(string heading, string field)
    {
        var map = ColumnMap.Build([heading, "Other"], new Dictionary<string, string[]>
        {
            ["Name"] = ["Name", "Item", "Description", "Product"],
            ["Price"] = ["Price", "Selling Price"],
            ["Barcode"] = ["Barcode", "UPC"],
        });
        Assert.Equal("value", map.Get(new ImportRow(2, ["value", "x"]), field));
    }

    [Fact]
    public void Exact_headings_win_and_cost_columns_are_never_the_price()
    {
        var map = ColumnMap.Build(["Item Code", "Item Description", "Cost Price"], new Dictionary<string, string[]>
        {
            ["Name"] = ["Name", "Item", "Description"],
            ["Price"] = ["Price", "Retail"],
            ["Code"] = ["Code", "Item Code"],
        });
        var row = new ImportRow(2, ["101", "Coke", "0.80"]);

        Assert.Equal("101", map.Get(row, "Code"));
        Assert.Equal("Coke", map.Get(row, "Name"));
        Assert.False(map.Has("Price"));
    }

    [Fact]
    public void Reads_csv_with_semicolons_bom_and_quoted_commas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shmesh-import-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "﻿Name;Price;Notes\r\n\r\nHot Dog;3.50;\"with mustard, onions\"\r\nWater;1.50;\r\n", new UTF8Encoding(false));
        try
        {
            var table = ImportTable.Read(path);
            Assert.Equal(["Name", "Price", "Notes"], table.Headers);
            Assert.Equal(2, table.Rows.Count);
            Assert.Equal("with mustard, onions", table.Rows[0].Cells[2]);
            Assert.Equal(3, table.Rows[0].Line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unsupported_file_type_is_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<PosException>(() => ImportTable.Read("list.pdf"));
        Assert.Contains("Excel", ex.Message);
    }
}

[Collection(PostgresTestBase.CollectionName)]
public class ImportIntegrationTests : PostgresTestBase
{
    private ImportService Importer() => new(DbFactory, Session, Options.Create(new PosOptions { TerminalName = "TEST-1" }),
        TimeProvider.System, NullLogger<ImportService>.Instance);

    private static ImportTable Table(params string?[][] rows) => ImportTable.FromRows("list.xlsx", rows);

    [PostgresFact]
    public async Task Product_import_creates_updates_and_reports_problems_per_row()
    {
        var table = Table(
            ["Item", "Dept", "Retail Price", "PLU", "UPC", "Tax %"],
            ["Lemonade", "Summer Drinks", "$2.75", "900", "0123456789012", "8.875%"],   // new + new category
            ["Coke", null, "2.10", "101", null, null],                                  // update price by code
            ["Water", "Drinks", "1.50", "105", null, "0"],                              // unchanged
            ["Bad Price", "Snacks", "abc", null, null, null],                           // error
            ["Clash", "Snacks", "1.00", "102", "DRI-103", null],                        // code is Diet Coke's, UPC column holds Pepsi's SKU
            ["Twin", "Snacks", "1.00", "900", null, null],                              // code used earlier in file
            [null, null, null, null, null, null]);                                      // blank row ignored

        var plan = await Importer().PlanProductsAsync(table);

        Assert.Equal([ImportAction.Create, ImportAction.Update, ImportAction.Unchanged, ImportAction.Error, ImportAction.Error, ImportAction.Error],
            plan.Rows.Select(r => r.Action));
        Assert.Contains(plan.Notes, n => n.Contains("Summer Drinks"));
        Assert.Contains("already belongs to Pepsi", plan.Rows[4].Detail);
        Assert.Contains("row 2", plan.Rows[5].Detail);

        var result = await Importer().ApplyAsync(plan);
        Assert.Equal(new ImportResult(1, 1, 3, 1), result);

        await using var db = DbFactory.CreateDbContext();
        var lemonade = await db.Products.Include(p => p.Category).SingleAsync(p => p.Name == "Lemonade");
        Assert.Equal(2.75m, lemonade.Price);
        Assert.Equal(0.08875m, lemonade.TaxRate);
        Assert.Equal("Summer Drinks", lemonade.Category!.Name);
        Assert.Equal("0123456789012", lemonade.Barcode);
        Assert.Equal(2.10m, (await db.Products.SingleAsync(p => p.Code == "101")).Price);

        // Importing the same file again changes nothing.
        var again = await Importer().PlanProductsAsync(table);
        Assert.False(again.HasWork);
        Assert.Equal(3, again.UnchangedCount);
    }

    [PostgresFact]
    public async Task Matching_code_with_a_different_name_is_flagged_as_a_rename()
    {
        var plan = await Importer().PlanProductsAsync(Table(["Name", "Code", "Price"], ["Bagel Deluxe", "101", "3.00"]));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(ImportAction.Update, row.Action);
        Assert.Contains("RENAMES “Coke” → “Bagel Deluxe”", row.Detail);
        Assert.Contains(plan.Notes, n => n.Contains("RENAMED"));
    }

    [PostgresFact]
    public async Task New_product_without_category_goes_to_uncategorized()
    {
        var plan = await Importer().PlanProductsAsync(Table(["Name", "Price"], ["Mystery Snack", "0.99"]));
        await Importer().ApplyAsync(plan);

        await using var db = DbFactory.CreateDbContext();
        var product = await db.Products.Include(p => p.Category).SingleAsync(p => p.Name == "Mystery Snack");
        Assert.Equal(ImportService.DefaultCategoryName, product.Category!.Name);
    }

    [PostgresFact]
    public async Task Customer_import_joins_names_assigns_accounts_and_records_opening_balances()
    {
        var customers = new CustomerService(DbFactory, Session, Options.Create(new PosOptions()), TimeProvider.System, NullLogger<CustomerService>.Instance);
        var existing = await customers.SaveCustomerAsync(new CustomerInput(0, "A-1", "Ava Stone", null, null, null, null, true));

        var table = Table(
            ["Acct", "First Name", "Last Name", "Mobile", "Credit Limit", "Owes"],
            [null, "Dana", "Levi", "555-0142", "75", "12.50"],       // new, auto account, opening balance
            ["A-1", "Ava", "Stone", "555-0100", null, "99"],        // update phone; balance ignored
            ["B-7", "Ben", "Adler", null, "lots", null],            // bad limit
            ["B-8", null, null, "555", null, null]);                // no name

        var plan = await Importer().PlanCustomersAsync(table);

        Assert.Equal([ImportAction.Create, ImportAction.Update, ImportAction.Error, ImportAction.Error], plan.Rows.Select(r => r.Action));
        Assert.Contains("ignored", plan.Rows[1].Detail);

        var result = await Importer().ApplyAsync(plan);
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);

        await using var db = DbFactory.CreateDbContext();
        var dana = await db.Customers.SingleAsync(c => c.Name == "Dana Levi");
        Assert.Equal("1001", dana.AccountNumber);
        Assert.Equal(75m, dana.CreditLimit);
        Assert.Equal(12.50m, dana.Balance);
        var opening = await db.CustomerTransactions.SingleAsync(t => t.CustomerId == dana.Id);
        Assert.Equal(12.50m, opening.Amount);
        Assert.Contains("Opening balance", opening.Note);

        var ava = await db.Customers.SingleAsync(c => c.Id == existing.Id);
        Assert.Equal("555-0100", ava.Phone);
        Assert.Equal(0m, ava.Balance);
    }

    [PostgresFact]
    public async Task Missing_name_column_is_explained_and_cashiers_cannot_import()
    {
        var ex = await Assert.ThrowsAsync<PosException>(() => Importer().PlanProductsAsync(Table(["Price", "Barcode"], ["1", "2"])));
        Assert.Contains("Name", ex.Message);

        Session.SignIn(new User { Id = 1, Username = "c", DisplayName = "C", PinHash = "x", Role = UserRole.Cashier });
        await Assert.ThrowsAsync<PosException>(() => Importer().PlanCustomersAsync(Table(["Name"], ["X"])));
    }
}
