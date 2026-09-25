using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShmeshPOS.Data;

/// <summary>
/// Used only by <c>dotnet ef</c>. Adding a migration does not need a live database; applying one from the
/// command line uses the SHMESHPOS_ConnectionStrings__ShmeshPos environment variable when it is set.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PosDbContext>
{
    public PosDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SHMESHPOS_ConnectionStrings__ShmeshPos")
            ?? "Host=localhost;Database=shmesh_pos;Username=postgres";

        var builder = new DbContextOptionsBuilder<PosDbContext>();
        DbConfigurator.Configure(builder, connectionString);
        return new PosDbContext(builder.Options);
    }
}
