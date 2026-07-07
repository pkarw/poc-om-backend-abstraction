using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Lib;

namespace OpenMercato.Modules.Directory.Cli;

/// <summary>
/// <c>add-org</c> — port of upstream cli.ts addOrganization. Creates an organization together with an
/// implicit tenant ("&lt;name&gt; Tenant") and rebuilds the tenant hierarchy.
/// Usage: add-org --name &lt;organization name&gt;
/// </summary>
public sealed class AddOrgCommand : ICliCommand
{
    public string Name => "add-org";
    public string Description => "Create an organization (with an implicit tenant): --name <organization name>";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var name = parsed.Get("name", "orgName");
        if (name is null)
        {
            Console.Error.WriteLine("Usage: add-org --name <organization name>");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = $"{name} Tenant", IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.Set<Tenant>().Add(tenant);
        await db.SaveChangesAsync();

        var org = new Organization
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = name,
            IsActive = true,
            Depth = 0,
            AncestorIdsJson = "[]",
            ChildIdsJson = "[]",
            DescendantIdsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<Organization>().Add(org);
        await db.SaveChangesAsync();

        await OrganizationHierarchy.RebuildForTenantAsync(db, tenant.Id);

        Console.WriteLine($"Organization created with id {org.Id} in tenant {tenant.Id}");
        return 0;
    }
}
