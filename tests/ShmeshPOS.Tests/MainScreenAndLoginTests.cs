using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

[Collection(PostgresTestBase.CollectionName)]
public class MainScreenAndLoginTests : PostgresTestBase
{
    private CatalogAdminService Catalog() => new(DbFactory, Session, TimeProvider.System, NullLogger<CatalogAdminService>.Instance);

    private AuthenticationService Auth(SessionService? session = null) => new(DbFactory, session ?? new SessionService(),
        Options.Create(new PosOptions()), TimeProvider.System, NullLogger<AuthenticationService>.Instance);

    private static ProductInput Input(Product p, bool onMain) =>
        new(p.Id, p.Name, p.CategoryId, p.Code, p.Sku, p.Barcode, p.Price, p.TaxRate, p.SortOrder, p.IsActive, onMain);

    [PostgresFact]
    public async Task Main_screen_holds_at_most_ten_products()
    {
        var products = await Catalog().GetProductsAsync();
        Assert.Equal(CatalogAdminService.MaxMainScreenItems, products.Count(p => p.ShowOnMainScreen)); // seeded

        var extra = products.First(p => !p.ShowOnMainScreen);
        var ex = await Assert.ThrowsAsync<PosException>(() => Catalog().SaveProductAsync(Input(extra, true)));
        Assert.Contains("already has 10", ex.Message);

        // Free a slot, then the extra product fits.
        await Catalog().SaveProductAsync(Input(products.First(p => p.ShowOnMainScreen), false));
        await Catalog().SaveProductAsync(Input(extra, true));

        var catalog = new ProductService(DbFactory, NullLogger<ProductService>.Instance);
        await catalog.LoadAsync();
        Assert.Equal(10, catalog.MainScreenProducts.Count);
        Assert.Contains(catalog.MainScreenProducts, p => p.Id == extra.Id);
    }

    [PostgresFact]
    public async Task Login_checks_the_pin_of_the_chosen_user_only()
    {
        var users = new UserAdminService(DbFactory, Session, TimeProvider.System, NullLogger<UserAdminService>.Instance);
        var sam = await users.SaveUserAsync(new UserInput(0, "sam", "Sam", UserRole.Cashier, true), "5829");
        await users.SaveUserAsync(new UserInput(0, "gone", "Gone", UserRole.Cashier, false), "7391");
        var admin = Session.CurrentUser!;

        var list = await Auth().GetLoginUsersAsync();
        Assert.Equal(["Administrator", "Sam"], list.Select(u => u.DisplayName));

        var session = new SessionService();
        Assert.True((await Auth(session).LoginAsync(sam.Id, "5829")).Success);
        Assert.Equal(sam.Id, session.CurrentUser!.Id);

        // The admin's correct PIN does not open Sam's account, and disabled users can't log in.
        Assert.False((await Auth().LoginAsync(sam.Id, AdminPin)).Success);
        Assert.True((await Auth().LoginAsync(admin.Id, AdminPin)).Success);
        var gone = await DbFactory.CreateDbContext().Users.SingleAsync(u => u.Username == "gone");
        Assert.False((await Auth().LoginAsync(gone.Id, "7391")).Success);
    }

    [PostgresFact]
    public async Task Repeated_wrong_pins_lock_the_terminal()
    {
        var auth = Auth();
        var admin = Session.CurrentUser!;
        for (var i = 0; i < 5; i++)
            await auth.LoginAsync(admin.Id, "0000");

        var result = await auth.LoginAsync(admin.Id, AdminPin);
        Assert.False(result.Success);
        Assert.Contains("Too many attempts", result.ErrorMessage);
    }
}
