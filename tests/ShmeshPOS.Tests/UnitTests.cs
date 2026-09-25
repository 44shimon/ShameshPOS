using ShmeshPOS.Models;
using ShmeshPOS.Security;
using ShmeshPOS.Services;
using ShmeshPOS.ViewModels;

namespace ShmeshPOS.Tests;

public class PinHasherTests
{
    [Fact]
    public void Hash_verifies_correct_pin_and_rejects_others()
    {
        var hash = PinHasher.Hash("4821");

        Assert.True(PinHasher.Verify("4821", hash));
        Assert.False(PinHasher.Verify("4822", hash));
        Assert.False(PinHasher.Verify("", hash));
        Assert.DoesNotContain("4821", hash);
    }

    [Fact]
    public void Same_pin_gets_a_different_salt_each_time()
    {
        Assert.NotEqual(PinHasher.Hash("1234"), PinHasher.Hash("1234"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("PBKDF2-SHA256$abc$salt$hash")]
    [InlineData("PBKDF2-SHA256$1000$!!notbase64$!!")]
    public void Malformed_hash_fails_closed(string stored)
    {
        Assert.False(PinHasher.Verify("1234", stored));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData(null, false)]
    public void Pin_format(string? pin, bool valid) => Assert.Equal(valid, PinHasher.IsValidPinFormat(pin));
}

public class RolePermissionsTests
{
    [Fact]
    public void Cashier_can_sell_but_not_manage()
    {
        Assert.True(RolePermissions.Has(UserRole.Cashier, Permission.Sell));
        Assert.True(RolePermissions.Has(UserRole.Cashier, Permission.TakePayment));
        Assert.False(RolePermissions.Has(UserRole.Cashier, Permission.ManageProducts));
        Assert.False(RolePermissions.Has(UserRole.Cashier, Permission.ExitApplication));
    }

    [Fact]
    public void Administrator_has_every_permission()
    {
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(RolePermissions.Has(UserRole.Administrator, p)));
    }
}

public class SaleCalculatorTests
{
    [Fact]
    public void Totals_sum_lines_and_round_tax_per_line()
    {
        var totals = SaleCalculator.Calculate(
        [
            (2.00m, 2, 0m),        // 4.00
            (1.50m, 1, 0.0875m),   // 1.50 + 0.13125 → 0.13
            (0.99m, 3, 0.0875m),   // 2.97 + 0.259875 → 0.26
        ]);

        Assert.Equal(8.47m, totals.Subtotal);
        Assert.Equal(0.39m, totals.Tax);
        Assert.Equal(8.86m, totals.Total);
    }

    [Fact]
    public void Money_rounds_half_away_from_zero()
    {
        Assert.Equal(0.13m, SaleCalculator.RoundMoney(0.125m));
        Assert.Equal(0.12m, SaleCalculator.RoundMoney(0.1249m));
    }

    [Theory]
    [InlineData("18.50", "20.00", "1.50")]
    [InlineData("18.50", "18.50", "0")]
    public void Change_is_cash_minus_total(string total, string cash, string expected) =>
        Assert.Equal(decimal.Parse(expected), SaleCalculator.CalculateChange(decimal.Parse(total), decimal.Parse(cash)));

    [Fact]
    public void Insufficient_cash_has_no_change()
    {
        Assert.Null(SaleCalculator.CalculateChange(18.50m, 18.49m));
    }
}

public class CartViewModelTests
{
    private static Product P(int id, decimal price, decimal tax = 0) =>
        new() { Id = id, Name = $"P{id}", Price = price, TaxRate = tax };

    [Fact]
    public void Adding_same_product_increments_quantity()
    {
        var cart = new CartViewModel();
        var coke = P(1, 2.00m);

        cart.Add(coke);
        cart.Add(coke);
        cart.Add(P(2, 1.50m));

        Assert.Equal(2, cart.Lines.Count);
        Assert.Equal(2, cart.Lines[0].Quantity);
        Assert.Equal(3, cart.ItemCount);
        Assert.Equal(5.50m, cart.Total);
        Assert.Same(cart.Lines[1], cart.SelectedLine);
    }

    [Fact]
    public void Decreasing_to_zero_removes_line_and_selects_neighbour()
    {
        var cart = new CartViewModel();
        cart.Add(P(1, 1m));
        var middle = cart.Add(P(2, 2m));
        cart.Add(P(3, 3m));

        cart.Decrease(middle);

        Assert.Equal([1, 3], cart.Lines.Select(l => l.ProductId));
        Assert.Equal(3, cart.SelectedLine!.ProductId);
        Assert.Equal(4m, cart.Total);
    }

    [Fact]
    public void Quantity_is_capped()
    {
        var cart = new CartViewModel();
        var line = cart.Add(P(1, 1m));

        cart.SetQuantity(line, 5000);

        Assert.Equal(SaleService.MaxLineQuantity, line.Quantity);
    }

    [Fact]
    public void Clear_empties_cart()
    {
        var cart = new CartViewModel();
        cart.Add(P(1, 1m));

        cart.Clear();

        Assert.True(cart.IsEmpty);
        Assert.Equal(0m, cart.Total);
        Assert.Null(cart.SelectedLine);
    }

    [Fact]
    public void Sale_lines_snapshot_name_price_and_tax()
    {
        var cart = new CartViewModel();
        cart.Add(P(7, 2.25m, 0.05m), 3);

        var line = Assert.Single(cart.ToSaleLines());
        Assert.Equal(new SaleLineInput(7, "P7", 2.25m, 0.05m, 3), line);
    }
}

public class PaymentViewModelTests
{
    [Fact]
    public void Quick_cash_offers_exact_then_next_bills()
    {
        Assert.Equal([7.25m, 8m, 10m, 20m, 50m], PaymentViewModel.BuildQuickCashAmounts(7.25m));
        Assert.Equal([20m, 50m, 100m], PaymentViewModel.BuildQuickCashAmounts(20m));
    }
}

public class WeakPinTests
{
    [Theory]
    [InlineData("1111", true)]
    [InlineData("1234", true)]
    [InlineData("98765", true)]
    [InlineData("1357", false)]
    [InlineData("5829", false)]
    [InlineData("1123", false)]
    public void Detects_repeated_and_sequential_pins(string pin, bool weak) => Assert.Equal(weak, PinHasher.IsWeakPin(pin));
}
