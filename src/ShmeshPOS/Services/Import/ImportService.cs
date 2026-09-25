using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services.Import;

public enum ImportAction
{
    Create,
    Update,
    Unchanged,
    Error,
}

public sealed record ImportPlanRow(int Line, string Name, ImportAction Action, string Detail);

/// <summary>What an import will do, row by row, before anything is written.</summary>
public sealed class ImportPlan
{
    internal ImportPlan(string fileName, string kind, IReadOnlyList<string> columns, IReadOnlyList<ImportPlanRow> rows,
        IReadOnlyList<string> notes, IReadOnlyList<object> changes)
    {
        FileName = fileName;
        Kind = kind;
        Columns = columns;
        Rows = rows;
        Notes = notes;
        Changes = changes;
    }

    public string FileName { get; }
    public string Kind { get; }

    /// <summary>The fields that were recognised in the file's header row.</summary>
    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<ImportPlanRow> Rows { get; }

    /// <summary>File-level remarks, e.g. categories that will be created.</summary>
    public IReadOnlyList<string> Notes { get; }

    internal IReadOnlyList<object> Changes { get; }

    public int CreateCount => Rows.Count(r => r.Action == ImportAction.Create);
    public int UpdateCount => Rows.Count(r => r.Action == ImportAction.Update);
    public int UnchangedCount => Rows.Count(r => r.Action == ImportAction.Unchanged);
    public int ErrorCount => Rows.Count(r => r.Action == ImportAction.Error);
    public bool HasWork => CreateCount + UpdateCount > 0;
}

public sealed record ImportResult(int Created, int Updated, int Skipped, int CategoriesCreated);

public interface IImportService
{
    Task<ImportPlan> PlanProductsAsync(ImportTable table, CancellationToken cancellationToken = default);
    Task<ImportPlan> PlanCustomersAsync(ImportTable table, CancellationToken cancellationToken = default);

    /// <summary>Writes every Create/Update row of the plan in one transaction. Error rows are skipped.</summary>
    Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default);

    /// <summary>CSV text with the recognised headings and example rows, for people starting a new list.</summary>
    string ProductTemplate();
    string CustomerTemplate();
}

public class ImportService(
    IDbContextFactory<PosDbContext> dbFactory,
    ISessionService session,
    IOptions<PosOptions> options,
    TimeProvider clock,
    ILogger<ImportService> logger) : IImportService
{
    public const string DefaultCategoryName = "Uncategorized";
    private const string ProductsKind = "products";
    private const string CustomersKind = "customers";

    // Heading synonyms, most specific first. Headings are compared ignoring case, spaces and punctuation.
    private static readonly Dictionary<string, string[]> ProductColumns = new()
    {
        ["Name"] = ["Name", "Product Name", "Item Name", "Product", "Item", "Description", "Title"],
        ["Price"] = ["Price", "Unit Price", "Sell Price", "Selling Price", "Retail Price", "Retail", "Sale Price", "Amount"],
        ["Category"] = ["Category", "Category Name", "Department", "Dept", "Group", "Type"],
        ["Code"] = ["Code", "Product Code", "Item Code", "PLU", "Item Number", "Item No", "Number", "No"],
        ["Sku"] = ["SKU", "Stock Code", "Stock Number"],
        ["Barcode"] = ["Barcode", "Bar Code", "UPC", "EAN", "GTIN"],
        ["Tax"] = ["Tax Rate", "Tax", "Tax %", "VAT", "Sales Tax"],
        ["Active"] = ["Active", "Available", "Enabled", "For Sale"],
        ["Order"] = ["Button Order", "Sort Order", "Sort", "Order", "Position"],
    };

    private static readonly Dictionary<string, string[]> CustomerColumns = new()
    {
        ["Name"] = ["Name", "Customer Name", "Full Name", "Customer", "Account Name"],
        ["FirstName"] = ["First Name", "First", "Given Name"],
        ["LastName"] = ["Last Name", "Last", "Surname", "Family Name"],
        ["Account"] = ["Account Number", "Account No", "Account", "Acct", "Customer Number", "Customer ID", "Card", "Card Number", "ID", "Number", "No"],
        ["Phone"] = ["Phone", "Phone Number", "Telephone", "Tel", "Mobile", "Cell"],
        ["Email"] = ["Email", "Email Address", "E-mail", "Mail"],
        ["Notes"] = ["Notes", "Note", "Comments", "Comment", "Remarks"],
        ["Limit"] = ["Credit Limit", "Limit", "Max Balance", "Maximum Balance"],
        ["Balance"] = ["Balance", "Opening Balance", "Owes", "Owed", "Amount Due", "Due"],
        ["Active"] = ["Active", "Open", "Status", "Enabled"],
    };

    // ============================ Products ============================

    private sealed record ProductChange(
        int Line, int? ExistingId, string Name, string? CategoryName, string? Code, string? Sku, string? Barcode,
        decimal? Price, decimal? TaxRate, bool? IsActive, int? SortOrder);

    public async Task<ImportPlan> PlanProductsAsync(ImportTable table, CancellationToken cancellationToken = default)
    {
        Require(Permission.ManageProducts, "import products");
        var map = ColumnMap.Build(table.Headers, ProductColumns);
        if (!map.Has("Name"))
            throw new PosException($"{table.FileName} needs a column for the item name (e.g. Name, Item, Product or Description). Headings found: {map.HeadingList}.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await db.Products.AsNoTracking().Include(p => p.Category).ToListAsync(cancellationToken);
        var categories = await db.Categories.AsNoTracking().ToListAsync(cancellationToken);

        // Code, SKU and barcode share one lookup, so any key identifies exactly one product.
        var byKey = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
            foreach (var key in new[] { p.Code, p.Sku, p.Barcode }.OfType<string>())
                byKey.TryAdd(key, p);
        var byName = products.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var categoryNames = new HashSet<string>(categories.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var newCategories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var keysInFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchedInFile = new Dictionary<int, int>();
        var rows = new List<ImportPlanRow>();
        var changes = new List<object>();

        foreach (var row in table.Rows)
        {
            var name = map.Get(row, "Name");
            var code = ImportValue.Key(map.Get(row, "Code"));
            var sku = ImportValue.Key(map.Get(row, "Sku"));
            var barcode = ImportValue.Key(map.Get(row, "Barcode"));
            var label = name ?? code ?? sku ?? barcode ?? "(no name)";
            var errors = new List<string>();

            decimal? price = null;
            if (map.Get(row, "Price") is { } priceText)
            {
                if (ImportValue.TryMoney(priceText, out var p) && p is >= 0 and <= CatalogAdminService.MaxPrice) price = p;
                else errors.Add($"price “{priceText}” isn't a valid amount");
            }
            decimal? tax = null;
            if (map.Get(row, "Tax") is { } taxText)
            {
                if (ImportValue.TryTaxRate(taxText, out var t)) tax = t;
                else errors.Add($"tax “{taxText}” isn't a valid rate");
            }
            bool? active = null;
            if (map.Get(row, "Active") is { } activeText)
            {
                if (ImportValue.TryBool(activeText, out var a)) active = a;
                else errors.Add($"active “{activeText}” should be yes or no");
            }
            int? order = null;
            if (map.Get(row, "Order") is { } orderText)
            {
                if (ImportValue.TryInt(orderText, out var o)) order = o;
                else errors.Add($"order “{orderText}” isn't a whole number");
            }
            if (name?.Length > 100) errors.Add("name is longer than 100 characters");
            if (code?.Length > 20) errors.Add("code is longer than 20 characters");
            if (sku?.Length > 50) errors.Add("SKU is longer than 50 characters");
            if (barcode?.Length > 50) errors.Add("barcode is longer than 50 characters");
            var categoryName = map.Get(row, "Category");
            if (categoryName?.Length > 60) errors.Add("category is longer than 60 characters");

            // Match an existing product: by any lookup key first, then by a unique name.
            Product? existing = null;
            foreach (var key in new[] { code, sku, barcode }.OfType<string>())
            {
                if (byKey.TryGetValue(key, out var hit)) { existing = hit; break; }
            }
            if (existing is null && name is not null && byName.TryGetValue(name, out var sameName))
            {
                if (sameName.Count == 1) existing = sameName[0];
                else errors.Add($"{sameName.Count} products are called “{name}”; add a code, SKU or barcode to say which");
            }

            // Keys must not belong to a different product, in the database or earlier in this file.
            foreach (var key in new[] { code, sku, barcode }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (byKey.TryGetValue(key, out var owner) && owner.Id != existing?.Id)
                    errors.Add($"“{key}” already belongs to {owner.Name}");
                else if (keysInFile.TryGetValue(key, out var otherLine))
                    errors.Add($"“{key}” is also used on row {otherLine}");
            }
            if (existing is not null && matchedInFile.TryGetValue(existing.Id, out var firstLine))
                errors.Add($"this is the same product as row {firstLine}");

            if (existing is null)
            {
                if (name is null) errors.Add("name is missing");
                if (price is null && !errors.Any(e => e.StartsWith("price"))) errors.Add("price is missing");
            }

            if (errors.Count > 0)
            {
                rows.Add(new ImportPlanRow(row.Line, label, ImportAction.Error, Capitalize(string.Join("; ", errors)) + "."));
                continue;
            }

            foreach (var key in new[] { code, sku, barcode }.OfType<string>())
                keysInFile.TryAdd(key, row.Line);
            if (existing is not null) matchedInFile[existing.Id] = row.Line;

            if (categoryName is not null && !categoryNames.Contains(categoryName)) newCategories.Add(categoryName);
            if (existing is null && categoryName is null && !categoryNames.Contains(DefaultCategoryName)) newCategories.Add(DefaultCategoryName);

            var change = new ProductChange(row.Line, existing?.Id, name ?? existing!.Name,
                categoryName ?? (existing is null ? DefaultCategoryName : null), code, sku, barcode, price, tax, active, order);

            if (existing is null)
            {
                rows.Add(new ImportPlanRow(row.Line, change.Name, ImportAction.Create,
                    $"New · {price:C} · {change.CategoryName}" + (tax is > 0 ? $" · tax {tax * 100:0.###}%" : "")));
                changes.Add(change);
                continue;
            }

            var diffs = DescribeProductChanges(existing, change);
            if (diffs.Count == 0)
            {
                rows.Add(new ImportPlanRow(row.Line, existing.Name, ImportAction.Unchanged, "Already up to date."));
                continue;
            }
            rows.Add(new ImportPlanRow(row.Line, existing.Name, ImportAction.Update, string.Join(" · ", diffs)));
            changes.Add(change);
        }

        var notes = new List<string>();
        var renames = rows.Count(r => r.Action == ImportAction.Update && r.Detail.Contains("RENAMES"));
        if (renames > 0)
            notes.Add($"{renames} existing product(s) have the same code, SKU or barcode as a row with a different name, and will be RENAMED. Check those rows before importing.");
        if (newCategories.Count > 0)
            notes.Add($"New categories will be created: {string.Join(", ", newCategories)}.");
        if (!map.Has("Price"))
            notes.Add("No Price column was found, so only existing products can be updated.");

        return new ImportPlan(table.FileName, ProductsKind, map.Describe(), rows, notes, changes);
    }

    private static List<string> DescribeProductChanges(Product p, ProductChange c)
    {
        var diffs = new List<string>();
        if (!string.Equals(p.Name, c.Name, StringComparison.OrdinalIgnoreCase)) diffs.Add($"RENAMES “{p.Name}” → “{c.Name}”");
        else if (!string.Equals(p.Name, c.Name, StringComparison.Ordinal)) diffs.Add($"name → {c.Name}");
        if (c.CategoryName is not null && !string.Equals(p.Category?.Name, c.CategoryName, StringComparison.OrdinalIgnoreCase)) diffs.Add($"category → {c.CategoryName}");
        if (c.Price is { } price && price != p.Price) diffs.Add($"price {p.Price:C} → {price:C}");
        if (c.TaxRate is { } tax && tax != p.TaxRate) diffs.Add($"tax {p.TaxRate * 100:0.###}% → {tax * 100:0.###}%");
        if (c.Code is not null && !string.Equals(c.Code, p.Code, StringComparison.OrdinalIgnoreCase)) diffs.Add($"code → {c.Code}");
        if (c.Sku is not null && !string.Equals(c.Sku, p.Sku, StringComparison.OrdinalIgnoreCase)) diffs.Add($"SKU → {c.Sku}");
        if (c.Barcode is not null && !string.Equals(c.Barcode, p.Barcode, StringComparison.OrdinalIgnoreCase)) diffs.Add($"barcode → {c.Barcode}");
        if (c.IsActive is { } active && active != p.IsActive) diffs.Add(active ? "available again" : "disabled");
        if (c.SortOrder is { } order && order != p.SortOrder) diffs.Add($"order → {order}");
        return diffs;
    }

    private async Task<ImportResult> ApplyProductsAsync(PosDbContext db, ImportPlan plan, User admin, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var changes = plan.Changes.Cast<ProductChange>().ToList();

        var categories = await db.Categories.ToListAsync(ct);
        var byName = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var nextSort = categories.Count == 0 ? 1 : categories.Max(c => c.SortOrder) + 1;
        var createdCategories = 0;
        Category CategoryFor(string name)
        {
            if (byName.TryGetValue(name, out var category)) return category;
            category = new Category { Name = name, SortOrder = nextSort++, Color = "#475569" };
            db.Categories.Add(category);
            byName[name] = category;
            createdCategories++;
            return category;
        }

        var ids = changes.Where(c => c.ExistingId is not null).Select(c => c.ExistingId!.Value).ToList();
        var existing = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        int created = 0, updated = 0;
        foreach (var c in changes)
        {
            Product product;
            if (c.ExistingId is { } id)
            {
                product = existing.GetValueOrDefault(id)
                    ?? throw new PosException($"The product on row {c.Line} was deleted while importing. Run the import again.");
                updated++;
            }
            else
            {
                product = new Product { Name = c.Name, CreatedAt = now, IsActive = true };
                db.Products.Add(product);
                created++;
            }

            product.Name = c.Name;
            if (c.CategoryName is not null) product.Category = CategoryFor(c.CategoryName);
            if (c.Price is { } price) product.Price = price;
            if (c.TaxRate is { } tax) product.TaxRate = tax;
            if (c.Code is not null) product.Code = c.Code;
            if (c.Sku is not null) product.Sku = c.Sku;
            if (c.Barcode is not null) product.Barcode = c.Barcode;
            if (c.IsActive is { } active) product.IsActive = active;
            if (c.SortOrder is { } order) product.SortOrder = order;
            product.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Admin {AdminId} imported products from {File}: {Created} created, {Updated} updated, {Skipped} rows skipped, {Categories} categories created",
            admin.Id, plan.FileName, created, updated, plan.ErrorCount, createdCategories);
        return new ImportResult(created, updated, plan.ErrorCount, createdCategories);
    }

    // ============================ Customers ============================

    private sealed record CustomerChange(
        int Line, int? ExistingId, string Name, string? AccountNumber, string? Phone, string? Email, string? Notes,
        decimal? CreditLimit, decimal? OpeningBalance, bool? IsActive);

    public async Task<ImportPlan> PlanCustomersAsync(ImportTable table, CancellationToken cancellationToken = default)
    {
        Require(Permission.ManageCustomers, "import customers");
        var map = ColumnMap.Build(table.Headers, CustomerColumns);
        if (!map.Has("Name") && !map.Has("FirstName") && !map.Has("LastName"))
            throw new PosException($"{table.FileName} needs a Name column (or First Name and Last Name). Headings found: {map.HeadingList}.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var customers = await db.Customers.AsNoTracking().ToListAsync(cancellationToken);
        var byAccount = customers.ToDictionary(c => c.AccountNumber, StringComparer.OrdinalIgnoreCase);
        var byName = customers.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var accountsInFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matchedInFile = new Dictionary<int, int>();
        var rows = new List<ImportPlanRow>();
        var changes = new List<object>();
        var ignoredBalances = 0;

        foreach (var row in table.Rows)
        {
            var joinedName = string.Join(" ", new[] { map.Get(row, "FirstName"), map.Get(row, "LastName") }.OfType<string>());
            var name = map.Get(row, "Name") ?? (joinedName.Length > 0 ? joinedName : null);
            var account = ImportValue.Key(map.Get(row, "Account"));
            var phone = map.Get(row, "Phone");
            var email = map.Get(row, "Email");
            var notes = map.Get(row, "Notes");
            var label = name ?? (account is null ? "(no name)" : $"#{account}");
            var errors = new List<string>();

            decimal? limit = null;
            if (map.Get(row, "Limit") is { } limitText)
            {
                if (ImportValue.TryMoney(limitText, out var l) && l is >= 0 and <= SettingsService.MaxCreditLimit) limit = l;
                else errors.Add($"credit limit “{limitText}” isn't a valid amount");
            }
            decimal? balance = null;
            if (map.Get(row, "Balance") is { } balanceText)
            {
                if (ImportValue.TryMoney(balanceText, out var b)) balance = b;
                else errors.Add($"balance “{balanceText}” isn't a valid amount");
            }
            bool? active = null;
            if (map.Get(row, "Active") is { } activeText)
            {
                if (ImportValue.TryBool(activeText, out var a)) active = a;
                else errors.Add($"active “{activeText}” should be yes or no");
            }

            if (name is null) errors.Add("name is missing");
            if (name?.Length > 100) errors.Add("name is longer than 100 characters");
            if (account is not null && (account.Length > 20 || !account.All(ch => char.IsLetterOrDigit(ch) || ch == '-')))
                errors.Add($"account number “{account}” can only use letters, numbers and dashes (up to 20)");
            if (phone?.Length > 30) errors.Add("phone is longer than 30 characters");
            if (email?.Length > 200) errors.Add("email is longer than 200 characters");
            if (notes?.Length > 500) errors.Add("notes are longer than 500 characters");

            // Match an existing customer: by account number, or (when the file has none) by a unique name.
            Customer? existing = null;
            if (account is not null)
            {
                byAccount.TryGetValue(account, out existing);
                if (accountsInFile.TryGetValue(account, out var otherLine)) errors.Add($"account {account} is also used on row {otherLine}");
            }
            else if (name is not null && byName.TryGetValue(name, out var sameName))
            {
                if (sameName.Count == 1) existing = sameName[0];
                else errors.Add($"{sameName.Count} customers are called “{name}”; add account numbers to say which");
            }
            if (existing is not null && matchedInFile.TryGetValue(existing.Id, out var firstLine))
                errors.Add($"this is the same customer as row {firstLine}");

            if (errors.Count > 0)
            {
                rows.Add(new ImportPlanRow(row.Line, label, ImportAction.Error, Capitalize(string.Join("; ", errors)) + "."));
                continue;
            }

            if (account is not null) accountsInFile[account] = row.Line;
            if (existing is not null) matchedInFile[existing.Id] = row.Line;

            if (existing is null)
            {
                var change = new CustomerChange(row.Line, null, name!, account, phone, email, notes, limit, balance is 0 ? null : balance, active);
                var detail = new List<string> { account is null ? "New · account number assigned automatically" : $"New · account {account}" };
                if (limit is { } l) detail.Add($"limit {l:C}");
                if (change.OpeningBalance is { } ob) detail.Add($"opening balance {ob:C}");
                rows.Add(new ImportPlanRow(row.Line, name!, ImportAction.Create, string.Join(" · ", detail)));
                changes.Add(change);
                continue;
            }

            var update = new CustomerChange(row.Line, existing.Id, name!, account, phone, email, notes, limit, null, active);
            var diffs = new List<string>();
            if (!string.Equals(existing.Name, update.Name, StringComparison.Ordinal)) diffs.Add($"name → {update.Name}");
            if (phone is not null && phone != existing.Phone) diffs.Add($"phone → {phone}");
            if (email is not null && email != existing.Email) diffs.Add($"email → {email}");
            if (notes is not null && notes != existing.Notes) diffs.Add("notes updated");
            if (limit is { } newLimit && newLimit != existing.CreditLimit) diffs.Add($"limit → {newLimit:C}");
            if (active is { } isActive && isActive != existing.IsActive) diffs.Add(isActive ? "reopened" : "closed");

            var balanceNote = "";
            if (balance is { } b2 && b2 != existing.Balance)
            {
                ignoredBalances++;
                balanceNote = $" (file balance {b2:C} ignored; account shows {existing.Balance:C})";
            }

            if (diffs.Count == 0)
            {
                rows.Add(new ImportPlanRow(row.Line, existing.Name, ImportAction.Unchanged, "Already up to date." + balanceNote));
                continue;
            }
            rows.Add(new ImportPlanRow(row.Line, existing.Name, ImportAction.Update, string.Join(" · ", diffs) + balanceNote));
            changes.Add(update);
        }

        var planNotes = new List<string>();
        if (map.Has("Balance"))
            planNotes.Add("Balances are recorded as an opening balance for new customers only. Existing customers keep their account balance; use Adjust balance for corrections.");
        if (ignoredBalances > 0)
            planNotes.Add($"{ignoredBalances} existing customer(s) have a different balance in the file. Those balances are not changed.");

        return new ImportPlan(table.FileName, CustomersKind, map.Describe(), rows, planNotes, changes);
    }

    private async Task<ImportResult> ApplyCustomersAsync(PosDbContext db, ImportPlan plan, User admin, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var changes = plan.Changes.Cast<CustomerChange>().ToList();

        var numbers = await db.Customers.Select(c => c.AccountNumber).ToListAsync(ct);
        var nextNumber = Math.Max(numbers.Select(n => int.TryParse(n, out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1, CustomerService.FirstAccountNumber);
        var taken = new HashSet<string>(numbers, StringComparer.OrdinalIgnoreCase);
        foreach (var c in changes.Where(c => c.AccountNumber is not null)) taken.Add(c.AccountNumber!);

        var ids = changes.Where(c => c.ExistingId is not null).Select(c => c.ExistingId!.Value).ToList();
        var existing = await db.Customers.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        int created = 0, updated = 0;
        foreach (var c in changes)
        {
            Customer customer;
            if (c.ExistingId is { } id)
            {
                customer = existing.GetValueOrDefault(id)
                    ?? throw new PosException($"The customer on row {c.Line} was deleted while importing. Run the import again.");
                if (c.Phone is not null) customer.Phone = c.Phone;
                if (c.Email is not null) customer.Email = c.Email;
                if (c.Notes is not null) customer.Notes = c.Notes;
                if (c.CreditLimit is { } limit) customer.CreditLimit = limit;
                updated++;
            }
            else
            {
                var account = c.AccountNumber;
                if (account is null)
                {
                    while (taken.Contains(nextNumber.ToString(CultureInfo.InvariantCulture))) nextNumber++;
                    account = nextNumber++.ToString(CultureInfo.InvariantCulture);
                    taken.Add(account);
                }
                customer = new Customer
                {
                    AccountNumber = account,
                    Name = c.Name,
                    Phone = c.Phone,
                    Email = c.Email,
                    Notes = c.Notes,
                    CreditLimit = c.CreditLimit,
                    CreatedAt = now,
                };
                db.Customers.Add(customer);
                created++;

                if (c.OpeningBalance is { } opening)
                {
                    var entry = CustomerLedger.Post(customer, CustomerTransactionType.Adjustment, opening, admin.Id, options.Value.TerminalName, now);
                    entry.Customer = customer;
                    entry.Note = $"Opening balance imported from {plan.FileName}";
                    db.CustomerTransactions.Add(entry);
                }
            }

            customer.Name = c.Name;
            if (c.IsActive is { } active) customer.IsActive = active;
            customer.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Admin {AdminId} imported customers from {File}: {Created} created, {Updated} updated, {Skipped} rows skipped",
            admin.Id, plan.FileName, created, updated, plan.ErrorCount);
        return new ImportResult(created, updated, plan.ErrorCount, 0);
    }

    // ============================ Shared ============================

    public async Task<ImportResult> ApplyAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        var admin = plan.Kind == ProductsKind
            ? Require(Permission.ManageProducts, "import products")
            : Require(Permission.ManageCustomers, "import customers");
        if (!plan.HasWork) return new ImportResult(0, 0, plan.ErrorCount, 0);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = plan.Kind == ProductsKind
                ? await ApplyProductsAsync(db, plan, admin, cancellationToken)
                : await ApplyCustomersAsync(db, plan, admin, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new PosException("Someone changed the same records while you were importing. Nothing was imported; run the import again.", ex);
        }
    }

    public string ProductTemplate() =>
        "Name,Category,Price,Tax Rate,Code,SKU,Barcode,Active\r\n" +
        "Coke,Drinks,2.00,0,101,DRI-101,049000000443,yes\r\n" +
        "Chips,Snacks,1.50,8.875,201,,,yes\r\n";

    public string CustomerTemplate() =>
        "Account Number,Name,Phone,Email,Credit Limit,Balance,Notes\r\n" +
        "1001,Dana Levi,555-0142,dana@example.com,75.00,12.50,\r\n" +
        ",Sam Cohen,555-0199,,,0,Account number left blank: assigned automatically\r\n";

    private User Require(Permission permission, string action)
    {
        if (session.CurrentUser is not { } user || !session.HasPermission(permission))
            throw new PosException($"You are not allowed to {action}.");
        return user;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
