using Microsoft.EntityFrameworkCore;

namespace ShmeshPOS.Data;

/// <summary>Shared DbContext options so the app and the EF design-time tools configure the provider identically.</summary>
public static class DbConfigurator
{
    public const string ConnectionStringName = "ShmeshPos";

    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
