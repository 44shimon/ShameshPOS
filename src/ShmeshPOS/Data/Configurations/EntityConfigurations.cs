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
    }
}

public class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_sales_total", "total = subtotal + tax"));
        b.Property(s => s.ReceiptNumber).HasMaxLength(20);
        b.Property(s => s.TerminalName).HasMaxLength(50);
        b.Property(s => s.Subtotal).HasPrecision(12, 2);
        b.Property(s => s.Tax).HasPrecision(12, 2);
        b.Property(s => s.Total).HasPrecision(12, 2);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);

        b.HasOne(s => s.Cashier)
            .WithMany()
            .HasForeignKey(s => s.CashierId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(s => s.ReceiptNumber).IsUnique();
        b.HasIndex(s => s.CreatedAt);
        b.HasIndex(s => s.CashierId);
    }
}

public class SaleItemConfiguration : IEntityTypeConfiguration<SaleItem>
{
    public void Configure(EntityTypeBuilder<SaleItem> b)
    {
        b.ToTable(t => t.HasCheckConstraint("ck_sale_items_quantity_positive", "quantity > 0"));
        b.Property(i => i.ProductName).HasMaxLength(100);
        b.Property(i => i.UnitPrice).HasPrecision(10, 2);
        b.Property(i => i.TaxRate).HasPrecision(6, 5);
        b.Property(i => i.TaxAmount).HasPrecision(12, 2);
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
