using Microsoft.EntityFrameworkCore;
using OpenMercato.Api;
using OpenMercato.Core.Configuration;
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
registry.ConfigureServices(builder.Services);

var app = builder.Build();

// The api container entrypoint applies migrations before serving traffic.
await MigrateAsync(app);

// Auth: env-gated, idempotent superadmin bootstrap (OM_INIT_SUPERADMIN_EMAIL/PASSWORD).
await AuthBootstrapSeeder.RunAsync(app.Services, app.Logger);

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
    const int maxAttempts = 10;
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
