using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Api;
using OpenMercato.Cli;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Core.Modules;
using OpenMercato.Core.Queue;
using StackExchange.Redis;

// Global, module-aware Open Mercato CLI. Dispatch model: `mercato <command> [args]`, aggregating the
// built-in commands (migrate/init/greenfield/seed) with every module's IModule.CliCommands. Mirrors
// upstream packages/cli. Reuses ModuleCatalog.CreateRegistry() + the shared AppDbContext wiring.

DotEnv.Load();
var config = AppConfig.FromEnvironment();
var registry = ModuleCatalog.CreateRegistry();

var services = new ServiceCollection();
services.AddLogging(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Warning)
    // The CLI reports failures via its own "Failed: <message>" line + exit code; suppress EF's
    // verbose connection/query error dumps so that message stays actionable (mirrors upstream).
    .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None));
services.AddSingleton(config);
services.AddSingleton(registry);
services.AddSingleton<INotificationCatalog, NotificationCatalog>();
services.AddSingleton<ICustomFieldRegistry, CustomFieldRegistry>();
// AppDbContext is the query context only; migrations are applied per-module via
// ModuleMigrations.ApplyAllAsync (see the migrate/greenfield commands).
services.AddDbContext<AppDbContext>((sp, options) => options
    .UseNpgsql(config.NpgsqlConnectionString)
    // Per-tenant-DEK field encryption: encrypt on write, decrypt on materialization (read).
    // Both no-op when no encryption map applies (e.g. non-relational provider / unprovisioned tenant).
    .AddInterceptors(
        sp.GetRequiredService<OpenMercato.Modules.Auth.Security.TenantEncryptionInterceptor>(),
        sp.GetRequiredService<OpenMercato.Modules.Auth.Security.TenantDecryptionMaterializationInterceptor>())
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
// Redis is optional for the CLI: the multiplexer is only constructed if a command resolves it, and
// AbortOnConnectFail=false means even then it never blocks when Redis is down.
services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(ConnectionStrings.FromRedisUrl(config.RedisUrl)));
services.AddSingleton<IJobQueue, RedisJobQueue>();
services.AddSingleton<IEventBus, LocalEventBus>();
registry.ConfigureServices(services);

using var provider = services.BuildServiceProvider();

var commands = CliCommandCatalog.BuiltIns()
    .Concat(registry.Modules.SelectMany(m => m.CliCommands))
    .ToList();

var commandName = args.Length > 0 ? args[0] : null;
if (commandName is null or "help" or "--help" or "-h")
{
    PrintHelp(commands);
    return commandName is null ? 1 : 0;
}

var command = commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparison.OrdinalIgnoreCase));
if (command is null)
{
    Console.Error.WriteLine($"Unknown command: {commandName}");
    Console.Error.WriteLine();
    PrintHelp(commands);
    return 1;
}

try
{
    return await command.RunAsync(args.Skip(1).ToArray(), provider);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed: {ex.Message}");
    return 1;
}

static void PrintHelp(IReadOnlyList<ICliCommand> commands)
{
    Console.WriteLine("Open Mercato CLI");
    Console.WriteLine("Usage: mercato <command> [args]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    foreach (var c in commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        Console.WriteLine($"  {c.Name,-16} {c.Description}");
}
