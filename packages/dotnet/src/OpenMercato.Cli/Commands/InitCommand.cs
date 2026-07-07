using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;

namespace OpenMercato.Cli.Commands;

/// <summary>
/// Built-in <c>init</c> — mirrors upstream <c>mercato init</c> / <c>yarn initialize</c>: apply
/// migrations, then run the OM-parity initial-tenant seeder.
/// Usage: init [--orgName <name>] [--email <email>] [--password <pw>] [--orgSlug <slug>]
/// Defaults: Acme Corp / superadmin@acme.com / secret / acme.
/// </summary>
public sealed class InitCommand : ICliCommand
{
    public string Name => "init";
    public string Description => "Migrate + seed the initial tenant (Acme Corp / superadmin@acme.com / secret / acme)";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            Console.WriteLine("Database migrations applied.");
        }
        return await SeedRunner.RunAsync(services, SeedOptions.Resolve(args));
    }
}
