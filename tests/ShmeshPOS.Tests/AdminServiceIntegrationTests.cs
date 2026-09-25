using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.Tests;

[Collection(PostgresTestBase.CollectionName)]
public class CatalogAdminServiceIntegrationTests : PostgresTestBase
{
    private CatalogAdminService CreateService() =>
        new(DbFactory, Session, TimeProvider.System, NullLogger<CatalogAdminService>.Instance);

    [PostgresFact]
    public async Task Creates_and_updates_a_product()
    {
        var service = CreateService();
        var category = (await service.GetCategoriesAsync()).First();

        var created = await service.SaveProductAsync(new ProductInput(0, "  Lemonade ", category.Id, "900", "DRK-LEM", "0123456789012", 2.50m, 0.08875m, 3, true));
        Assert.Equal("Lemonade", created.Name);

        await service.SaveProductAsync(new ProductInput(created.Id, "Lemonade", category.Id, "900", "", null, 2.75m, 0m, 3, false));

        await using var db = DbFactory.CreateDbContext();
        var saved = await db.Products.SingleAsync(p => p.Id == created.Id);
        Assert.Equal(2.75m, saved.Price);
        Assert.Null(saved.Sku);
        Assert.False(saved.IsActive);
        Assert.True(saved.UpdatedAt >= saved.CreatedAt);
    }

    [PostgresFact]
    public async Task Lookup_keys_are_unique_across_code_sku_and_barcode_ignoring_case()
    {
        var service = CreateService();
        var products = await service.GetProductsAsync();
        var coke = products.First(p => p.Name == "Coke");

        // Another product's SKU entered as a barcode must be rejected, even in a different case.
        var ex = await Assert.ThrowsAsync<PosException>(() => service.SaveProductAsync(
            new ProductInput(0, "Clash", coke.CategoryId, null, null, coke.Sku!.ToLowerInvariant(), 1m, 0m, 0, true)));
        Assert.Contains("Coke", ex.Message);

        // Saving a product with its own existing keys is fine.
        await service.SaveProductAsync(new ProductInput(coke.Id, coke.Name, coke.CategoryId, coke.Code, coke.Sku, null, 2.10m, 0m, 1, true));
    }

    [PostgresFact]
    public async Task Rejects_invalid_price_and_tax()
    {
        var service = CreateService();
        var category = (await service.GetCategoriesAsync()).First();

        await Assert.ThrowsAsync<PosException>(() => service.SaveProductAsync(new ProductInput(0, "X", category.Id, null, null, null, -1m, 0m, 0, true)));
        await Assert.ThrowsAsync<PosException>(() => service.SaveProductAsync(new ProductInput(0, "X", category.Id, null, null, null, 1.005m, 0m, 0, true)));
        await Assert.ThrowsAsync<PosException>(() => service.SaveProductAsync(new ProductInput(0, "X", category.Id, null, null, null, 1m, 1m, 0, true)));
    }

    [PostgresFact]
    public async Task Category_names_are_unique_ignoring_case()
    {
        var service = CreateService();
        var created = await service.SaveCategoryAsync(new CategoryInput(0, "Hot Drinks", "#7C3AED", 9, true));

        await Assert.ThrowsAsync<PosException>(() => service.SaveCategoryAsync(new CategoryInput(0, "hot drinks", null, 10, true)));
        await Assert.ThrowsAsync<PosException>(() => service.SaveCategoryAsync(new CategoryInput(created.Id, "Hot Drinks", "blue", 9, true)));
    }

    [PostgresFact]
    public async Task Cashier_cannot_change_products()
    {
        Session.SignIn(new User { Id = 999, Username = "c", DisplayName = "C", PinHash = "x", Role = UserRole.Cashier });
        var category = (await CreateService().GetCategoriesAsync()).First();

        await Assert.ThrowsAsync<PosException>(() => CreateService().SaveProductAsync(
            new ProductInput(0, "Nope", category.Id, null, null, null, 1m, 0m, 0, true)));
    }
}

[Collection(PostgresTestBase.CollectionName)]
public class UserAdminServiceIntegrationTests : PostgresTestBase
{
    private UserAdminService CreateService() =>
        new(DbFactory, Session, TimeProvider.System, NullLogger<UserAdminService>.Instance);

    [PostgresFact]
    public async Task Creates_cashier_who_can_then_log_in()
    {
        var created = await CreateService().SaveUserAsync(new UserInput(0, "sam", "Sam", UserRole.Cashier, true), "5829");

        var auth = new AuthenticationService(DbFactory, new SessionService(),
            Microsoft.Extensions.Options.Options.Create(new ShmeshPOS.Infrastructure.PosOptions()),
            TimeProvider.System, NullLogger<AuthenticationService>.Instance);
        var result = await auth.LoginWithPinAsync("5829");

        Assert.True(result.Success);
        Assert.Equal(created.Id, result.User!.Id);
        Assert.Equal(UserRole.Cashier, result.User.Role);
    }

    [PostgresFact]
    public async Task Pin_must_be_unique_across_users()
    {
        var ex = await Assert.ThrowsAsync<PosException>(() =>
            CreateService().SaveUserAsync(new UserInput(0, "dup", "Dup", UserRole.Cashier, true), AdminPin));
        Assert.Contains("already in use", ex.Message);
    }

    [PostgresFact]
    public async Task Weak_and_malformed_pins_are_rejected()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(0, "a1", "A", UserRole.Cashier, true), "1111"));
        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(0, "a2", "A", UserRole.Cashier, true), "4321"));
        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(0, "a3", "A", UserRole.Cashier, true), "12a4"));
        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(0, "a4", "A", UserRole.Cashier, true), null));
    }

    [PostgresFact]
    public async Task Admin_cannot_lock_themselves_out()
    {
        var me = Session.CurrentUser!;
        var service = CreateService();

        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(me.Id, me.Username, me.DisplayName, UserRole.Cashier, true), null));
        await Assert.ThrowsAsync<PosException>(() => service.SaveUserAsync(new UserInput(me.Id, me.Username, me.DisplayName, UserRole.Administrator, false), null));
    }

    [PostgresFact]
    public async Task Last_active_administrator_cannot_be_removed_by_another_admin()
    {
        var service = CreateService();
        var original = Session.CurrentUser!;
        var second = await service.SaveUserAsync(new UserInput(0, "boss2", "Second Admin", UserRole.Administrator, true), "7310");

        // Second admin demotes the first: allowed, because the second admin remains.
        Session.SignIn(second);
        await service.SaveUserAsync(new UserInput(original.Id, original.Username, original.DisplayName, UserRole.Cashier, true), null);

        // Now the second admin is the only one and cannot be demoted by anyone (including via a stale session).
        Session.SignIn(new User { Id = 12345, Username = "ghost", DisplayName = "G", PinHash = "x", Role = UserRole.Administrator });
        var ex = await Assert.ThrowsAsync<PosException>(() =>
            service.SaveUserAsync(new UserInput(second.Id, second.Username, second.DisplayName, UserRole.Cashier, true), null));
        Assert.Contains("At least one active administrator", ex.Message);
    }

    [PostgresFact]
    public async Task Usernames_are_unique_ignoring_case()
    {
        await Assert.ThrowsAsync<PosException>(() =>
            CreateService().SaveUserAsync(new UserInput(0, "ADMIN", "Other", UserRole.Cashier, true), "5830"));
    }
}
