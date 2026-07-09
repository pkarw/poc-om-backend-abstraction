using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.QueryIndex;

/// <summary>
/// Migrations-only DbContext for the <c>query_index</c> module. Each module owns its schema history
/// (table <c>__ef_migrations_query_index</c>) and its own migrations assembly (<c>OpenMercato.Modules.QueryIndex</c>), mirroring Open Mercato's
/// per-module migrations. This context has an intentionally empty model — the module's tables are
/// created by hand-written raw-SQL migrations, not model diffing. It is NOT a query context;
/// <see cref="OpenMercato.Core.Data.AppDbContext"/> remains the single runtime query context.
/// </summary>
public sealed class QueryIndexMigrationsDbContext : DbContext
{
    public QueryIndexMigrationsDbContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Intentionally empty: schema is applied via raw-SQL migrations in this assembly.
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef</c> tooling against the <c>query_index</c> module. Reads
/// DATABASE_URL via <see cref="AppConfig"/> and points EF at this module's migrations assembly and
/// history table.
/// </summary>
public sealed class QueryIndexMigrationsDbContextFactory : IDesignTimeDbContextFactory<QueryIndexMigrationsDbContext>
{
    public QueryIndexMigrationsDbContext CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var config = AppConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<QueryIndexMigrationsDbContext>()
            .UseNpgsql(config.NpgsqlConnectionString, o => o
                .MigrationsAssembly("OpenMercato.Modules.QueryIndex")
                .MigrationsHistoryTable("__ef_migrations_query_index"))
            .Options;
        return new QueryIndexMigrationsDbContext(options);
    }
}
