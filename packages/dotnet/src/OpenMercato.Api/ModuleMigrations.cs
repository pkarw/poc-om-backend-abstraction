using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenMercato.Core;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Currencies;
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Dashboards;
using OpenMercato.Modules.Dictionaries;
using OpenMercato.Modules.Directory;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.HealthCheck;
using OpenMercato.Modules.QueryIndex;

namespace OpenMercato.Api;

/// <summary>
/// Applies every ported module's migrations, each against its OWN migrations DbContext, migrations
/// assembly and history table (mirrors Open Mercato, where each module owns its migrations). Runs in
/// module dependency order (the same order as <see cref="ModuleCatalog"/>). Does NOT touch
/// <see cref="OpenMercato.Core.Data.AppDbContext"/> — that stays the runtime query context.
///
/// Lives in OpenMercato.Api because this is the composition root that references every module project
/// (Core cannot, as modules depend on Core). OpenMercato.Cli reaches it via its reference to Api.
/// </summary>
public static class ModuleMigrations
{
    /// <summary>
    /// Constructs each module's migrations context in dependency order and applies its pending
    /// migrations. Tolerates a cold Postgres on first boot (DNS + init can exceed 20s) by retrying
    /// each module's migrate step.
    /// </summary>
    public static async Task ApplyAllAsync(string npgsqlConn, ILogger? logger = null, CancellationToken ct = default)
    {
        await MigrateModuleAsync("health_check", "OpenMercato.Modules.HealthCheck", "__ef_migrations_health_check",
            o => new HealthCheckMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("auth", "OpenMercato.Modules.Auth", "__ef_migrations_auth",
            o => new AuthMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("directory", "OpenMercato.Modules.Directory", "__ef_migrations_directory",
            o => new DirectoryMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("dashboards", "OpenMercato.Modules.Dashboards", "__ef_migrations_dashboards",
            o => new DashboardsMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("audit_logs", "OpenMercato.Core", "__ef_migrations_audit_logs",
            o => new AuditLogsMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("entities", "OpenMercato.Modules.Entities", "__ef_migrations_entities",
            o => new EntitiesMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("query_index", "OpenMercato.Modules.QueryIndex", "__ef_migrations_query_index",
            o => new QueryIndexMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("currencies", "OpenMercato.Modules.Currencies", "__ef_migrations_currencies",
            o => new CurrenciesMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("dictionaries", "OpenMercato.Modules.Dictionaries", "__ef_migrations_dictionaries",
            o => new DictionariesMigrationsDbContext(o), npgsqlConn, logger, ct);
        await MigrateModuleAsync("customers", "OpenMercato.Modules.Customers", "__ef_migrations_customers",
            o => new CustomersMigrationsDbContext(o), npgsqlConn, logger, ct);

        logger?.LogInformation("All module migrations applied.");
    }

    private static async Task MigrateModuleAsync<TContext>(
        string moduleId,
        string migrationsAssembly,
        string historyTable,
        Func<DbContextOptions, TContext> create,
        string npgsqlConn,
        ILogger? logger,
        CancellationToken ct)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(npgsqlConn, o => o
                .MigrationsAssembly(migrationsAssembly)
                .MigrationsHistoryTable(historyTable))
            .Options;

        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = create(options);
                await db.Database.MigrateAsync(ct);
                logger?.LogInformation("Migrations applied for module {Module}.", moduleId);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger?.LogWarning(ex, "Migration attempt {Attempt}/{Max} for module {Module} failed; retrying in 2s.",
                    attempt, maxAttempts, moduleId);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
