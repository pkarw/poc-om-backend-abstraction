using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Directory.Cli;

/// <summary>
/// <c>list-orgs</c> — port of upstream cli.ts listOrganizations. Lists every organization with its
/// tenant id and creation date.
/// </summary>
public sealed class ListOrgsCommand : ICliCommand
{
    public string Name => "list-orgs";
    public string Description => "List all organizations";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orgs = await db.Set<Organization>().AsNoTracking().OrderBy(o => o.Name).ToListAsync();
        if (orgs.Count == 0)
        {
            Console.WriteLine("No organizations found");
            return 0;
        }

        Console.WriteLine($"Found {orgs.Count} organization(s):");
        Console.WriteLine();
        Console.WriteLine("ID                                   | Name                    | Tenant ID                            | Created");
        Console.WriteLine("-------------------------------------|-------------------------|--------------------------------------|-----------");

        foreach (var org in orgs)
        {
            var name = (org.Name.Length > 23 ? org.Name[..23] : org.Name).PadRight(23);
            var created = org.CreatedAt.ToString("yyyy-MM-dd");
            Console.WriteLine($"{org.Id,-36} | {name} | {org.TenantId,-36} | {created}");
        }
        return 0;
    }
}
