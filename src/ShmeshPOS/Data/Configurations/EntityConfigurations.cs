using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShmeshPOS.Models;

namespace ShmeshPOS.Data.Configurations;

// Table and column names are snake_cased by EFCore.NamingConventions; raw SQL in check
// constraints and index filters therefore uses snake_case names.

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.Property(u => u.Username).HasMaxLength(50);
        b.Property(u => u.DisplayName).HasMaxLength(100);
        b.Property(u => u.PinHash).HasMaxLength(200);
        b.Property(u => u.Role).HasConversion<string>().HasMaxLength(30);
        b.HasIndex(u => u.Username).IsUnique();
    }
}

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.Property(c => c.Name).HasMaxLength(60);
        b.Property(c => c.Color).HasMaxLength(9);
        b.HasIndex(c => c.Name).IsUnique();
        b.HasIndex(c => c.SortOrder);
    }
}

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_products_price_non_negative", "price >= 0");
            t.HasCheckConstraint("ck_products_tax_rate_range", "tax_rate >= 0 AND tax_rate < 1");
            t.HasCheckConstraint("ck_products_reorder_level_non_negative", "reorder_level IS NULL OR reorder_level >= 0");
        });

        b.Property(p => p.Name).HasMaxLength(100);
        b.Property(p => p.Code).HasMaxLength(20);
        b.Property(p => p.Sku).HasMaxLength(50);
        b.Property(p => p.Barcode).HasMaxLength(50);
        b.Property(p => p.Price).HasPrecision(10, 2);
        b.Property(p => p.TaxRate).HasPrecision(6, 5);

        b.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Lookup keys must be unique when present; NULLs may repeat.
        b.HasIndex(p => p.Code).IsUnique().HasFilter("code IS NOT NULL");
        b.HasIndex(p => p.Sku).IsUnique().HasFilter("sku IS NOT NULL");
        b.HasIndex(p => p.Barcode).IsUnique().HasFilter("barcode IS NOT NULL");
        b.HasIndex(p => p.Name);
        b.HasIndex(p => new { p.CategoryId, p.SortOrder });
        b.HasIndex(p => p.ShowOnMainScreen).HasFilter("show_on_main_screen");
    }
}

public class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sales_total", "total = subtotal + tax");
            t.HasCheckConstraint("ck_sales_discount_non_negative", "discount >= 0");
        });
        b.Property(s => s.ReceiptNumber).HasMaxLength(20);
        b.Property(s => s.TerminalName).HasMaxLength(50);
        b.Property(s => s.Subtotal).HasPrecision(12, 2);
        b.Property(s => s.Tax).HasPrecision(12, 2);
        b.Property(s => s.Total).HasPrecision(12, 2);
        b.Property(s => s.Discount).HasPrecision(12, 2);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        b.HasOne(s => s.Cashier)
            .WithMany()
            .HasForeignKey(s => s.CashierId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(s => s.Customer)
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(s => s.ReceiptNumber).IsUnique();
        b.HasIndex(s => s.CreatedAt);
        b.HasIndex(s => s.CashierId);
        b.HasIndex(s => s.CustomerId);
    }
}

public class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sale_items_quantity_positive", "quantity > 0");
            t.HasCheckConstraint("ck_sale_items_discounts", "discount >= 0 AND sale_discount_share >= 0 AND line_total >= 0");
        });
        b.Property(i => i.ProductName).HasMaxLength(100);
        b.Property(i => i.UnitPrice).HasPrecision(10, 2);
        b.Property(i => i.TaxRate).HasPrecision(6, 5);
        b.Property(i => i.TaxAmount).HasPrecision(12, 2);
        b.Property(i => i.Discount).HasPrecision(12, 2);
        b.Property(i => i.SaleDiscountShare).HasPrecision(12, 2);
        b.Property(i => i.LineTotal).HasPrecision(12, 2);

        b.HasOne(i => i.Sale)
            .WithMany(s => s.Items)
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(i => i.SaleId);
        b.HasIndex(i => i.ProductId);
    }
}

public class PaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> b)
    {
        b.Property(m => m.Code).HasMaxLength(30);
        b.Property(m => m.Name).HasMaxLength(60);
        b.Property(m => m.Kind).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(m => m.Code).IsUnique();
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_payments_amount_non_negative", "amount >= 0"));
        b.Property(p => p.Amount).HasPrecision(12, 2);
        b.Property(p => p.CashReceived).HasPrecision(12, 2);
        b.Property(p => p.ChangeGiven).HasPrecision(12, 2);

        b.HasOne(p => p.Sale)
            .WithMany(s => s.Payments)
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(p => p.PaymentMethod)
            .WithMany()
            .HasForeignKey(p => p.PaymentMethodId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(p => p.SaleId);
        b.HasIndex(p => p.PaymentMethodId);
    }
}

public class ReceiptSequenceConfiguration : IEntityTypeConfiguration<ReceiptSequence>
{
    public void Configure(EntityTypeBuilder<ReceiptSequence> b)
    {
        b.HasKey(r => r.Year);
        b.Property(r => r.Year).ValueGeneratedNever();
    }
}

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_customers_credit_limit_non_negative", "credit_limit IS NULL OR credit_limit >= 0"));
        b.Property(c => c.AccountNumber).HasMaxLength(20);
        b.Property(c => c.Name).HasMaxLength(100);
        b.Property(c => c.Phone).HasMaxLength(30);
        b.Property(c => c.Email).HasMaxLength(200);
        b.Property(c => c.Notes).HasMaxLength(500);
        b.Property(c => c.Balance).HasPrecision(12, 2);
        b.Property(c => c.CreditLimit).HasPrecision(12, 2);
        b.HasIndex(c => c.AccountNumber).IsUnique();
        b.HasIndex(c => c.Name);
    }
}

public class CustomerTransactionConfiguration : IEntityTypeConfiguration<CustomerTransaction>
{
    public void Configure(EntityTypeBuilder<CustomerTransaction> b)
    {
        b.Property(t => t.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(t => t.Amount).HasPrecision(12, 2);
        b.Property(t => t.BalanceAfter).HasPrecision(12, 2);
        b.Property(t => t.Note).HasMaxLength(500);
        b.Property(t => t.TerminalName).HasMaxLength(50);

        b.HasOne(t => t.Customer).WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.Sale).WithMany().HasForeignKey(t => t.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.PaymentMethod).WithMany().HasForeignKey(t => t.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(t => t.Refund).WithMany().HasForeignKey(t => t.RefundId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(t => new { t.CustomerId, t.CreatedAt });
        b.HasIndex(t => t.SaleId);
    }
}

public class StoreSettingConfiguration : IEntityTypeConfiguration<StoreSetting>
{
    public void Configure(EntityTypeBuilder<StoreSetting> b)
    {
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).HasMaxLength(100);
        b.Property(s => s.Value).HasMaxLength(1000);
    }
}

public class HeldSaleConfiguration : IEntityTypeConfiguration<HeldSale>
{
    public void Configure(EntityTypeBuilder<HeldSale> b)
    {
        b.Property(h => h.Label).HasMaxLength(60);
        b.Property(h => h.TerminalName).HasMaxLength(50);
        b.Property(h => h.Total).HasPrecision(12, 2);
        b.Property(h => h.SaleDiscountKind).HasConversion<string>().HasMaxLength(10);
        b.Property(h => h.SaleDiscountValue).HasPrecision(12, 2);
        b.HasOne(h => h.Cashier).WithMany().HasForeignKey(h => h.CashierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(h => h.Customer).WithMany().HasForeignKey(h => h.CustomerId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(h => h.CreatedAt);
    }
}

public class HeldSaleItemConfiguration : IEntityTypeConfiguration<HeldSaleItem>
{
    public void Configure(EntityTypeBuilder<HeldSaleItem> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_held_sale_items_quantity_positive", "quantity > 0"));
        b.Property(i => i.ProductName).HasMaxLength(100);
        b.Property(i => i.UnitPrice).HasPrecision(10, 2);
        b.Property(i => i.TaxRate).HasPrecision(6, 5);
        b.Property(i => i.DiscountKind).HasConversion<string>().HasMaxLength(10);
        b.Property(i => i.DiscountValue).HasPrecision(12, 2);
        b.HasOne(i => i.HeldSale).WithMany(h => h.Items).HasForeignKey(i => i.HeldSaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(i => i.Product).WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(i => i.HeldSaleId);
    }
}

public class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_refunds_total", "total = subtotal + tax");
            t.HasCheckConstraint("ck_refunds_total_non_negative", "total >= 0");
        });
        b.Property(r => r.RefundNumber).HasMaxLength(30);
        b.Property(r => r.TerminalName).HasMaxLength(50);
        b.Property(r => r.Reason).HasMaxLength(200);
        b.Property(r => r.Subtotal).HasPrecision(12, 2);
        b.Property(r => r.Tax).HasPrecision(12, 2);
        b.Property(r => r.Total).HasPrecision(12, 2);
        b.HasOne(r => r.OriginalSale).WithMany().HasForeignKey(r => r.OriginalSaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.Cashier).WithMany().HasForeignKey(r => r.CashierId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(r => r.PaymentMethod).WithMany().HasForeignKey(r => r.PaymentMethodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => r.RefundNumber).IsUnique();
        b.HasIndex(r => r.OriginalSaleId);
        b.HasIndex(r => r.CreatedAt);
    }
}

public class RefundItemConfiguration : IEntityTypeConfiguration<RefundItem>
{
    public void Configure(EntityTypeBuilder<RefundItem> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_refund_items_quantity_positive", "quantity > 0"));
        b.Property(i => i.ProductName).HasMaxLength(100);
        b.Property(i => i.UnitPrice).HasPrecision(10, 2);
        b.Property(i => i.TaxRate).HasPrecision(6, 5);
        b.Property(i => i.TaxAmount).HasPrecision(12, 2);
        b.Property(i => i.LineTotal).HasPrecision(12, 2);
        b.HasOne(i => i.Refund).WithMany(r => r.Items).HasForeignKey(i => i.RefundId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(i => i.SaleItem).WithMany().HasForeignKey(i => i.SaleItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(i => i.RefundId);
        b.HasIndex(i => i.SaleItemId);
    }
}

public class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable(t =>
        {
            t.HasCheckConstraint("ck_shifts_opening_float_non_negative", "opening_float >= 0");
            t.HasCheckConstraint("ck_shifts_counted_cash_non_negative", "counted_cash IS NULL OR counted_cash >= 0");
            t.HasCheckConstraint("ck_shifts_closed_complete", "(closed_at IS NULL) = (counted_cash IS NULL)");
        });
        b.Property(s => s.TerminalName).HasMaxLength(50);
        b.Property(s => s.OpeningFloat).HasPrecision(12, 2);
        b.Property(s => s.ExpectedCash).HasPrecision(12, 2);
        b.Property(s => s.CountedCash).HasPrecision(12, 2);
        b.Property(s => s.Note).HasMaxLength(200);
        b.Ignore(s => s.IsOpen);
        b.Ignore(s => s.Difference);
        b.HasOne(s => s.OpenedBy).WithMany().HasForeignKey(s => s.OpenedById).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(s => s.ClosedBy).WithMany().HasForeignKey(s => s.ClosedById).OnDelete(DeleteBehavior.Restrict);

        // One open shift per register; two registers opening at once can't both succeed.
        b.HasIndex(s => s.TerminalName).IsUnique().HasFilter("closed_at IS NULL").HasDatabaseName("ix_shifts_one_open_per_terminal");
        b.HasIndex(s => s.OpenedAt);
    }
}

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Note).HasMaxLength(200);
        b.Property(m => m.TerminalName).HasMaxLength(50);
        b.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.Sale).WithMany().HasForeignKey(m => m.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.Refund).WithMany().HasForeignKey(m => m.RefundId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(m => new { m.ProductId, m.CreatedAt });
        b.HasIndex(m => m.SaleId);
        b.HasIndex(m => m.RefundId);
    }
}
