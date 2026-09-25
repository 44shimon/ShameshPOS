using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;
using ShmeshPOS.Services;

namespace ShmeshPOS.Data;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Applies migrations and inserts first-run data. Every seed step is skipped when its table already has rows.</summary>
public class DatabaseInitializer(
    IDbContextFactory<PosDbContext> dbFactory,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<SeedOptions> seedOptions,
    TimeProvider clock,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (databaseOptions.Value.AutoMigrate)
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, pending);
                await db.Database.MigrateAsync(cancellationToken);
            }
        }

        await SeedPaymentMethodsAsync(db, cancellationToken);
        await SeedAdministratorAsync(db, cancellationToken);
        if (seedOptions.Value.SampleData)
            await SeedSampleCatalogAsync(db, cancellationToken);
    }

    private async Task SeedPaymentMethodsAsync(PosDbContext db, CancellationToken ct)
    {
        if (await db.PaymentMethods.AnyAsync(ct))
            return;

        db.PaymentMethods.AddRange(
            new PaymentMethod { Code = "CASH", Name = "Cash", Kind = PaymentKind.Cash, SortOrder = 1 },
            new PaymentMethod { Code = "CREDIT", Name = "Credit Card", Kind = PaymentKind.Card, SortOrder = 2 },
            new PaymentMethod { Code = "DEBIT", Name = "Debit Card", Kind = PaymentKind.Card, SortOrder = 3 },
            new PaymentMethod { Code = "OTHER", Name = "Other", Kind = PaymentKind.Other, SortOrder = 4 });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded default payment methods");
    }

    private async Task SeedAdministratorAsync(PosDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
            return;

        var seed = seedOptions.Value;
        if (!PinHasher.IsValidPinFormat(seed.AdminPin))
        {
            throw new PosException(
                $"No users exist yet. Set Seed:AdminPin ({PinHasher.MinPinLength}-{PinHasher.MaxPinLength} digits) in the configuration and restart.");
        }

        db.Users.Add(new User
        {
            Username = seed.AdminUsername,
            DisplayName = seed.AdminDisplayName,
            PinHash = PinHasher.Hash(seed.AdminPin),
            Role = UserRole.Administrator,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Created initial administrator {Username} from Seed:AdminPin. Change this PIN before going live.", seed.AdminUsername);
    }

    private async Task SeedSampleCatalogAsync(PosDbContext db, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(ct))
            return;

        var now = clock.GetUtcNow().UtcDateTime;
        var code = 100;

        Category Cat(string name, int sort, string color, params (string Name, decimal Price)[] items)
        {
            var category = new Category { Name = name, SortOrder = sort, Color = color };
            var i = 0;
            foreach (var (itemName, price) in items)
            {
                code++;
                category.Products.Add(new Product
                {
                    Name = itemName,
                    Code = code.ToString(),
                    Sku = $"{name[..3].ToUpperInvariant()}-{code}",
                    Price = price,
                    TaxRate = 0m,
                    SortOrder = ++i,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            return category;
        }

        db.Categories.AddRange(
            Cat("Drinks", 1, "#2563EB",
                ("Coke", 2.00m), ("Diet Coke", 2.00m), ("Pepsi", 2.00m), ("Sprite", 2.00m),
                ("Water", 1.50m), ("Orange Juice", 2.50m), ("Iced Tea", 2.00m), ("Coffee", 1.75m), ("Hot Tea", 1.50m)),
            Cat("Food", 2, "#DC2626",
                ("Hot Dog", 3.50m), ("Pizza Slice", 3.00m), ("Bagel", 2.25m), ("Sandwich", 5.00m),
                ("Soup", 3.50m), ("French Fries", 2.75m)),
            Cat("Snacks", 3, "#D97706",
                ("Chips", 1.50m), ("Pretzels", 1.50m), ("Popcorn", 2.00m), ("Cookie", 1.25m), ("Granola Bar", 1.25m)),
            Cat("Candy", 4, "#DB2777",
                ("Chocolate Bar", 1.50m), ("Gummies", 1.00m), ("Lollipop", 0.50m), ("Gum", 1.00m)),
            Cat("Other", 5, "#059669",
                ("Ice Cream", 2.50m), ("Fruit Cup", 2.00m), ("Pickle", 1.00m)));

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded sample canteen catalog");
    }
}
