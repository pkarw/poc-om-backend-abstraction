using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;

namespace OpenMercato.Cli.Commands;

/// <summary>Built-in <c>migrate</c> — applies all pending EF Core migrations (upstream `db migrate`).</summary>
public sealed class MigrateCommand : ICliCommand
{
    public string Name => "migrate";
    public string Description => "Apply all pending database migrations";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        Console.WriteLine("Database migrations applied.");
        return 0;
    }
}
