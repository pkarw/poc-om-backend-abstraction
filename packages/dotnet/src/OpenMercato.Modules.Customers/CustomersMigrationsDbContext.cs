using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.Customers;

/// <summary>
/// Migrations-only DbContext for the <c>customers</c> module. Each module owns its schema history
/// (table <c>__ef_migrations_customers</c>) and its own migrations assembly (<c>OpenMercato.Modules.Customers</c>), mirroring Open Mercato's
/// per-module migrations. This context has an intentionally empty model — the module's tables are
/// created by hand-written raw-SQL migrations, not model diffing. It is NOT a query context;
/// <see cref="OpenMercato.Core.Data.AppDbContext"/> remains the single runtime query context.
/// </summary>
public sealed class CustomersMigrationsDbContext : DbContext
{
    public CustomersMigrationsDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Intentionally empty: schema is applied via raw-SQL migrations in this assembly.
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef</c> tooling against the <c>customers</c> module. Reads
/// DATABASE_URL via <see cref="AppConfig"/> and points EF at this module's migrations assembly and
/// history table.
/// </summary>
public sealed class CustomersMigrationsDbContextFactory : IDesignTimeDbContextFactory<CustomersMigrationsDbContext>
{
    public CustomersMigrationsDbContext CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var config = AppConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<CustomersMigrationsDbContext>()
            .UseNpgsql(config.NpgsqlConnectionString, o => o
                .MigrationsAssembly("OpenMercato.Modules.Customers")
                .MigrationsHistoryTable("__ef_migrations_customers"))
            .Options;
        return new CustomersMigrationsDbContext(options);
    }
}
