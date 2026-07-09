using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Directory.Api;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Directory;

/// <summary>
/// The directory module (upstream packages/core/src/modules/directory). Owns the canonical
/// <c>tenants</c> and <c>organizations</c> tables plus the organization-hierarchy engine. The
/// byte-exact DDL is created by the raw-SQL migration in this module's Migrations/ folder
/// (AddDirectoryModule); ConfigureModel here only wires the runtime EF model (table/column names).
/// Directory has a mutual runtime dependency on auth (RBAC + tenant-selection guards) and is ported
/// together with it.
/// </summary>
public sealed class DirectoryModule : IModule
{
    public string Id => "directory";

    /// <summary>The 4 ACL feature ids (acl.ts). Kept for back-compat.</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "directory.tenants.view",
        "directory.tenants.manage",
        "directory.organizations.view",
        "directory.organizations.manage",
    };

    /// <summary>The 4 ACL features with their exact titles (upstream acl.ts, all module 'directory').</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("directory.tenants.view", "View tenants"),
        new AclFeatureDefinition("directory.tenants.manage", "Manage tenants"),
        new AclFeatureDefinition("directory.organizations.view", "View organizations"),
        new AclFeatureDefinition("directory.organizations.manage", "Manage organizations"),
    };

    // NotificationTypes: directory declares NONE (no notifications.ts).
    // CustomFieldSets: directory declares NONE (no ce.ts / data/fields.ts).

    /// <summary>Default role features (upstream setup.ts): superadmin manages all tenants; admin manages orgs.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["superadmin"] = new[] { "directory.tenants.*" },
            ["admin"] = new[] { "directory.organizations.view", "directory.organizations.manage" },
        };

    /// <summary>CLI subcommands (upstream directory-facing cli.ts): add-org, list-orgs.</summary>
    public IReadOnlyList<ICliCommand> CliCommands { get; } = new ICliCommand[]
    {
        new OpenMercato.Modules.Directory.Cli.AddOrgCommand(),
        new OpenMercato.Modules.Directory.Cli.ListOrgsCommand(),
    };

    /// <summary>The 6 declared CRUD events (events.ts, createModuleEvents moduleId 'directory'). All persistent.</summary>
    public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
    {
        new EventDeclaration("directory.tenant.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("directory.tenant.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("directory.tenant.deleted", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("directory.organization.created", "{ id, tenantId, organizationId }", true),
        new EventDeclaration("directory.organization.updated", "{ id, tenantId, organizationId }", true),
        new EventDeclaration("directory.organization.deleted", "{ id, tenantId, organizationId }", true),
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // di.ts is a no-op placeholder upstream — directory registers no services of its own.
        // It consumes dataEngine/em/cache/rbacService/kmsService registered elsewhere.
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id).HasName("tenants_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id).HasName("organizations_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Slug).HasColumnName("slug");
            e.Property(x => x.LogoUrl).HasColumnName("logo_url");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.Property(x => x.RootId).HasColumnName("root_id");
            e.Property(x => x.TreePath).HasColumnName("tree_path");
            e.Property(x => x.Depth).HasColumnName("depth");
            e.Property(x => x.AncestorIdsJson).HasColumnName("ancestor_ids").HasColumnType("jsonb");
            e.Property(x => x.ChildIdsJson).HasColumnName("child_ids").HasColumnType("jsonb");
            e.Property(x => x.DescendantIdsJson).HasColumnName("descendant_ids").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Discover every IDirectoryRouteGroup in the Directory assembly (parity with AuthModule).
        var groupType = typeof(IDirectoryRouteGroup);
        var implementations = typeof(DirectoryModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && groupType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in implementations)
        {
            if (Activator.CreateInstance(type) is IDirectoryRouteGroup group)
                group.Map(routes);
        }
    }
}
