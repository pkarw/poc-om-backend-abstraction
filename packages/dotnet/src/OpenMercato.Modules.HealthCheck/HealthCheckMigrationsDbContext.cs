using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.HealthCheck;

/// <summary>
/// Migrations-only DbContext for the <c>health_check</c> module. Each module owns its schema history
/// (table <c>__ef_migrations_health_check</c>) and its own migrations assembly (<c>OpenMercato.Modules.HealthCheck</c>), mirroring Open Mercato's
/// per-module migrations. This context has an intentionally empty model — the module's tables are
/// created by hand-written raw-SQL migrations, not model diffing. It is NOT a query context;
/// <see cref="OpenMercato.Core.Data.AppDbContext"/> remains the single runtime query context.
/// </summary>
public sealed class HealthCheckMigrationsDbContext : DbContext
{
    public HealthCheckMigrationsDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Intentionally empty: schema is applied via raw-SQL migrations in this assembly.
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef</c> tooling against the <c>health_check</c> module. Reads
/// DATABASE_URL via <see cref="AppConfig"/> and points EF at this module's migrations assembly and
/// history table.
/// </summary>
public sealed class HealthCheckMigrationsDbContextFactory : IDesignTimeDbContextFactory<HealthCheckMigrationsDbContext>
{
    public HealthCheckMigrationsDbContext CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var config = AppConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<HealthCheckMigrationsDbContext>()
            .UseNpgsql(config.NpgsqlConnectionString, o => o
                .MigrationsAssembly("OpenMercato.Modules.HealthCheck")
                .MigrationsHistoryTable("__ef_migrations_health_check"))
            .Options;
        return new HealthCheckMigrationsDbContext(options);
    }
}
