using Microsoft.EntityFrameworkCore;
using ShmeshPOS.Models;

namespace ShmeshPOS.Data;

public class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ReceiptSequence> ReceiptSequences => Set<ReceiptSequence>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerTransaction> CustomerTransactions => Set<CustomerTransaction>();
    public DbSet<StoreSetting> StoreSettings => Set<StoreSetting>();
    public DbSet<HeldSale> HeldSales => Set<HeldSale>();
    public DbSet<HeldSaleItem> HeldSaleItems => Set<HeldSaleItem>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);
    }
}
