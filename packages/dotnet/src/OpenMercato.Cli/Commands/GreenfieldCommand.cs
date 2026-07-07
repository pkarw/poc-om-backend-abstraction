using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;

namespace OpenMercato.Cli.Commands;

/// <summary>
/// Built-in <c>greenfield</c> — mirrors upstream <c>db greenfield</c> / <c>dev:greenfield</c>: DROP and
/// recreate the public schema, re-apply all migrations, then seed. DESTRUCTIVE: deletes all data.
/// Usage: greenfield --yes [--orgName ...] [--email ...] [--password ...] [--orgSlug ...]
/// </summary>
public sealed class GreenfieldCommand : ICliCommand
{
    public string Name => "greenfield";
    public string Description => "DROP + recreate schema, migrate and seed (destructive — requires --yes)";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        if (parsed.Get("yes") is null)
        {
            Console.Error.WriteLine("This command will DELETE all data. Use --yes to confirm.");
            return 1;
        }

        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Console.WriteLine("Dropping and recreating schema 'public'…");
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;");
            await db.Database.MigrateAsync();
            Console.WriteLine("Database migrations applied.");
        }
        return await SeedRunner.RunAsync(services, SeedOptions.Resolve(args));
    }
}
