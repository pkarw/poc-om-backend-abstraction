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
builder.Services.AddDbContext<AppDbContext>(options =>
    options
        .UseNpgsql(config.NpgsqlConnectionString, npgsql => npgsql.MigrationsAssembly("OpenMercato.Api"))
        // Modules with byte-exact parity ship hand-written raw-SQL migrations, so the EF model
        // snapshot intentionally does not describe their tables. Ignore the resulting drift warning.
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
// In the OM testbench (docker-compose.testbench), Open Mercato OWNS the shared schema and seeds
// it, so the .NET API runs migrations-off (OM_SKIP_MIGRATIONS=1) and does not seed. Its byte-exact
// ports read/write the same auth/directory/dashboards tables OM created.
var skipMigrations = (Environment.GetEnvironmentVariable("OM_SKIP_MIGRATIONS") ?? "")
    .Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
if (skipMigrations)
    app.Logger.LogInformation("OM_SKIP_MIGRATIONS set — running against an externally-owned schema (no migrate, no seed).");
else
{
    await MigrateAsync(app);

    // Env-gated, idempotent boot seeding of the full Acme dataset (OM_INIT_SUPERADMIN_EMAIL/PASSWORD).
    // Identical to CLI `init`/`seed` — see OpenMercato.Modules.Directory.Seeding.InitialTenantSeeder.
    await OpenMercato.Modules.Directory.Seeding.InitialTenantSeeder.RunBootAsync(app.Services, app.Logger);
}

// Liveness: must not touch Postgres or Redis.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "dotnet-api" }));

// Module routes (each module maps under /api/<module_id>/...).
registry.MapRoutes(app);

app.Logger.LogInformation("dotnet-api listening on port {Port}. Modules: {Modules}",
    config.Port, string.Join(", ", registry.Modules.Select(m => m.Id)));

app.Run();

static async Task MigrateAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Tolerate a cold Postgres on first `make up` (DNS + init can exceed 20s).
    const int maxAttempts = 30;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            app.Logger.LogInformation("Database migrations applied.");
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            app.Logger.LogWarning(ex, "Migration attempt {Attempt}/{Max} failed; retrying in 2s.",
                attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
