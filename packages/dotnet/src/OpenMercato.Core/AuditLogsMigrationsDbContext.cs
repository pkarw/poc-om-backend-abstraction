using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Core;

/// <summary>
/// Migrations-only DbContext for the <c>audit_logs</c> module. Each module owns its schema history
/// (table <c>__ef_migrations_audit_logs</c>) and its own migrations assembly (<c>OpenMercato.Core</c>), mirroring Open Mercato's
/// per-module migrations. This context has an intentionally empty model — the module's tables are
/// created by hand-written raw-SQL migrations, not model diffing. It is NOT a query context;
/// <see cref="OpenMercato.Core.Data.AppDbContext"/> remains the single runtime query context.
/// </summary>
public sealed class AuditLogsMigrationsDbContext : DbContext
{
    public AuditLogsMigrationsDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Intentionally empty: schema is applied via raw-SQL migrations in this assembly.
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef</c> tooling against the <c>audit_logs</c> module. Reads
/// DATABASE_URL via <see cref="AppConfig"/> and points EF at this module's migrations assembly and
/// history table.
/// </summary>
public sealed class AuditLogsMigrationsDbContextFactory : IDesignTimeDbContextFactory<AuditLogsMigrationsDbContext>
{
    public AuditLogsMigrationsDbContext CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var config = AppConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<AuditLogsMigrationsDbContext>()
            .UseNpgsql(config.NpgsqlConnectionString, o => o
                .MigrationsAssembly("OpenMercato.Core")
                .MigrationsHistoryTable("__ef_migrations_audit_logs"))
            .Options;
        return new AuditLogsMigrationsDbContext(options);
    }
}
