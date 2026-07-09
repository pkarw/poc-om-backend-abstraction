using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Api;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Modules;

namespace OpenMercato.Cli.Commands;

/// <summary>Built-in <c>migrate</c> — applies all pending EF Core migrations (upstream `db migrate`).</summary>
public sealed class MigrateCommand : ICliCommand
{
    public string Name => "migrate";
    public string Description => "Apply all pending database migrations";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var config = services.GetRequiredService<AppConfig>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("migrate");
        // Per-module migrations: each module owns its migrations context + history table.
        await ModuleMigrations.ApplyAllAsync(config.NpgsqlConnectionString, logger);
        Console.WriteLine("Database migrations applied.");
        return 0;
    }
}
