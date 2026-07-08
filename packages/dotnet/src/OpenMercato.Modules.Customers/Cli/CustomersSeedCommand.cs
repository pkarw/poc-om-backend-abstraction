using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Customers.Seeding;

namespace OpenMercato.Modules.Customers.Cli;

/// <summary>
/// <c>customers seed</c> — installs the 5 CE field sets (install-from-CE), seeds the tag free pool,
/// a dictionary subset, and the example companies/people (Phase-1 subset of upstream
/// <c>customers seed-dictionaries</c> + <c>seed-examples</c>). Optional <c>--tenant &lt;uuid&gt;</c> /
/// <c>--org &lt;uuid&gt;</c>; defaults to the first non-deleted tenant + its root organization.
/// </summary>
public sealed class CustomersSeedCommand : ICliCommand
{
    public string Name => "customers-seed";
    public string Description => "Seed customers Phase-1 data (CE defs, tags, dictionary subset, example people/companies).";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var indexer = scope.ServiceProvider.GetRequiredService<ICrudIndexer>();

        Guid tenantId;
        var tenantArg = parsed.Get("tenant", "tenantId");
        if (tenantArg is not null && Guid.TryParse(tenantArg, out var t)) tenantId = t;
        else
        {
            var first = await db.Database.SqlQueryRaw<Guid>("SELECT id FROM tenants WHERE deleted_at IS NULL ORDER BY created_at LIMIT 1").ToListAsync();
            if (first.Count == 0) { Console.Error.WriteLine("customers seed: no tenants found."); return 1; }
            tenantId = first[0];
        }

        Guid organizationId;
        var orgArg = parsed.Get("org", "organizationId");
        if (orgArg is not null && Guid.TryParse(orgArg, out var o)) organizationId = o;
        else
        {
            var orgs = await db.Database.SqlQueryRaw<Guid>(
                "SELECT id FROM organizations WHERE tenant_id = {0} AND deleted_at IS NULL ORDER BY depth, created_at LIMIT 1", tenantId).ToListAsync();
            if (orgs.Count == 0) { Console.Error.WriteLine("customers seed: no organizations for tenant."); return 1; }
            organizationId = orgs[0];
        }

        var seeded = await CustomersSeeder.SeedAsync(db, registry, indexer, tenantId, organizationId);
        Console.WriteLine($"customers seed: tenant={tenantId} org={organizationId} records={seeded}");
        return 0;
    }
}
