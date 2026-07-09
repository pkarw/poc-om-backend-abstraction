using Microsoft.EntityFrameworkCore;
using OpenMercato.Api;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Core.Queue;
using OpenMercato.Modules.Auth;
using StackExchange.Redis;

DotEnv.Load();
var config = AppConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");

var registry = ModuleCatalog.CreateRegistry();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(registry);
// Declared-surface catalogs built from module declarations (upstream notifications.ts / ce.ts).
// PORT-TODO: delivery/EAV-storage engines arrive with the notifications/entities module ports.
builder.Services.AddSingleton<OpenMercato.Core.Modules.INotificationCatalog, OpenMercato.Core.Modules.NotificationCatalog>();
builder.Services.AddSingleton<OpenMercato.Core.Modules.ICustomFieldRegistry, OpenMercato.Core.Modules.CustomFieldRegistry>();
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    // AppDbContext is the runtime QUERY context only; it no longer owns migrations. Each module
    // applies its own raw-SQL migrations through ModuleMigrations.ApplyAllAsync (per-module context +
    // history table). The model snapshot intentionally does not describe module tables, so ignore the
    // resulting drift warning (relevant only if this context were ever migrated/EnsureCreated'd).
    options
        .UseNpgsql(config.NpgsqlConnectionString)
        // Per-tenant-DEK field encryption on write (no-op when no encryption map applies).
        .AddInterceptors(sp.GetRequiredService<OpenMercato.Modules.Auth.Security.TenantEncryptionInterceptor>())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(ConnectionStrings.FromRedisUrl(config.RedisUrl)));
builder.Services.AddSingleton<IJobQueue>(sp =>
    new RedisJobQueue(config.QueueRedisUrl == config.RedisUrl
        ? sp.GetRequiredService<IConnectionMultiplexer>()
        : ConnectionMultiplexer.Connect(ConnectionStrings.FromRedisUrl(config.QueueRedisUrl))));
builder.Services.AddSingleton<IEventBus, LocalEventBus>();
// CRUD factory extension points (no-op custom-fields/indexer + fail-closed auth bridge). Registered
// before modules so the Auth module can override ICrudRequestContext with its real implementation.
builder.Services.AddOpenMercatoCrud();
registry.ConfigureServices(builder.Services);

var app = builder.Build();

// The api container entrypoint applies migrations before serving traffic.
//
// Two schema-ownership modes:
//   • Standalone (default): the .NET API owns the schema — migrate, then seed the full dataset.
//   • Testbench (OM_SKIP_MIGRATIONS=1): Open Mercato OWNS + migrates the shared schema; the .NET
//     API runs migrations-off. Historically it also skipped seeding and relied on OM's `mercato
//     init` — but OM seeds customer PII with per-tenant DEKs the port can't read. So when
//     OM_SEED_ON_BOOT=1 the .NET API seeds the shared schema itself (after OM has migrated it),
//     making the ported modules' data self-consistent (write and read with the same crypto).
var skipMigrations = EnvFlag("OM_SKIP_MIGRATIONS");
var seedOnBoot = EnvFlag("OM_SEED_ON_BOOT");

if (skipMigrations)
    app.Logger.LogInformation("OM_SKIP_MIGRATIONS set — running against an externally-owned schema (no migrate).");
else
    await MigrateAsync(app);

// Seed when we own the schema (normal boot) or when explicitly asked to seed an externally-owned
// one (testbench). For an externally-owned schema, first wait until OM has created the tables.
if (!skipMigrations || seedOnBoot)
{
    if (skipMigrations)
        await WaitForSchemaAsync(app);
    await SeedAllAsync(app);
}

static bool EnvFlag(string name) =>
    (Environment.GetEnvironmentVariable(name) ?? "").Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

// Liveness: must not touch Postgres or Redis.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "dotnet-api" }));

// Module routes (each module maps under /api/<module_id>/...).
registry.MapRoutes(app);

app.Logger.LogInformation("dotnet-api listening on port {Port}. Modules: {Modules}",
    config.Port, string.Join(", ", registry.Modules.Select(m => m.Id)));

app.Run();

static async Task MigrateAsync(WebApplication app)
{
    // Per-module migrations: each module owns its migrations context + history table. The cold-Postgres
    // retry logic lives inside ApplyAllAsync (tolerates a cold DB on first `make up`).
    var config = app.Services.GetRequiredService<AppConfig>();
    await ModuleMigrations.ApplyAllAsync(config.NpgsqlConnectionString, app.Logger);
}

// Wait for an externally-owned schema (testbench: OM migrates it) to appear before seeding.
// The dotnet-api container also depends_on the om-app healthcheck, so this is a belt-and-suspenders
// poll for the last-needed tables rather than the primary ordering mechanism.
static async Task WaitForSchemaAsync(WebApplication app)
{
    // Every table the seeders write to across the ported modules — wait for all of them so we never
    // seed against a half-migrated schema regardless of OM's per-module migration ordering.
    var required = new[]
    {
        "tenants", "organizations", "users", "roles", "role_acls",
        "currencies", "dashboard_role_widgets",
        "custom_field_defs", "custom_field_values", "entity_indexes",
        "customer_entities", "customer_pipelines", "customer_deals", "customer_dictionary_entries",
    };
    const int maxAttempts = 150; // ~5 min at 2s
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var missing = new List<string>();
            foreach (var table in required)
            {
                var present = await db.Database
                    .SqlQueryRaw<string?>("SELECT to_regclass('public.' || {0})::text AS \"Value\"", table)
                    .ToListAsync();
                if (present.Count == 0 || present[0] is null) missing.Add(table);
            }
            if (missing.Count == 0)
            {
                app.Logger.LogInformation("External schema ready — all seed-target tables present.");
                return;
            }
            if (attempt % 5 == 1)
                app.Logger.LogInformation("Waiting for external schema; missing tables: {Missing} (attempt {Attempt}/{Max}).",
                    string.Join(", ", missing), attempt, maxAttempts);
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            app.Logger.LogWarning(ex, "Schema probe {Attempt}/{Max} failed; retrying in 2s.", attempt, maxAttempts);
        }
        if (attempt >= maxAttempts)
            throw new InvalidOperationException("Timed out waiting for the externally-owned schema to be migrated.");
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

// Idempotent boot seeding, mirroring `mercato init`: first provision the initial tenant/org/users/
// roles/ACLs (the port of core setupInitialTenant), then run every module's own setup hooks
// (onTenantCreated → seedDefaults → seedExamples) in dependency order across each scope. Safe to run
// repeatedly (provisioning + every module hook guard their own rows).
static async Task SeedAllAsync(WebApplication app)
{
    // 1) core provisioning: the Acme tenant/org, roles, ACLs, superadmin/admin/employee users.
    await OpenMercato.Modules.Directory.Seeding.InitialTenantSeeder.RunBootAsync(app.Services, app.Logger);
    // 2) per-module data: currencies, dashboards, customers, … each owns its seedDefaults/seedExamples.
    await OpenMercato.Core.Modules.ModuleSeedRunner.RunAsync(app.Services, app.Logger, includeExamples: true);
}
