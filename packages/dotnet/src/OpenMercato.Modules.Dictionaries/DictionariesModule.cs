using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dictionaries.Api;
using OpenMercato.Modules.Dictionaries.Commands;
using OpenMercato.Modules.Dictionaries.Data;

namespace OpenMercato.Modules.Dictionaries;

/// <summary>
/// The dictionaries module (upstream packages/core/src/modules/dictionaries) — org-scoped
/// enumerations. Owns the <c>dictionaries</c> + <c>dictionary_entries</c> tables (byte-exact DDL in
/// the raw-SQL migration <c>20260707080000_AddDictionariesModule</c>); ConfigureModel wires the
/// runtime EF model only. All writes dispatch through the command bus (registered in
/// <see cref="ConfigureServices"/>); the routes are hand-written to preserve upstream's bespoke
/// <c>{ items }</c> response shapes (upstream never used <c>makeCrudRoute</c> here — see ADR 0019).
/// </summary>
public sealed class DictionariesModule : IModule
{
    public string Id => "dictionaries";

    /// <summary>The 2 ACL feature ids (acl.ts). Kept for back-compat.</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "dictionaries.view",
        "dictionaries.manage",
    };

    /// <summary>The 2 ACL features with their exact titles (upstream acl.ts, module 'dictionaries').</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("dictionaries.view", "View shared dictionaries"),
        new AclFeatureDefinition("dictionaries.manage", "Manage shared dictionaries"),
    };

    /// <summary>Default role features (upstream setup.ts): admin manages, employee views.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["admin"] = new[] { "dictionaries.view", "dictionaries.manage" },
            ["employee"] = new[] { "dictionaries.view" },
        };

    /// <summary>The 3 declared entry CRUD events (events.ts, moduleId 'dictionaries'). All persistent.</summary>
    public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
    {
        new EventDeclaration("dictionaries.entry.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("dictionaries.entry.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("dictionaries.entry.deleted", "{ id, organizationId, tenantId }", true),
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // Write ops are commands on the command bus (upstream commands/*.ts + the inline dictionary writes).
        services.AddScoped<ICommand, CreateDictionaryCommand>();
        services.AddScoped<ICommand, UpdateDictionaryCommand>();
        services.AddScoped<ICommand, DeleteDictionaryCommand>();
        services.AddScoped<ICommand, CreateDictionaryEntryCommand>();
        services.AddScoped<ICommand, UpdateDictionaryEntryCommand>();
        services.AddScoped<ICommand, DeleteDictionaryEntryCommand>();
        services.AddScoped<ICommand, ReorderDictionaryEntriesCommand>();
        services.AddScoped<ICommand, SetDefaultDictionaryEntryCommand>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Dictionary>(e =>
        {
            e.ToTable("dictionaries");
            e.HasKey(x => x.Id).HasName("dictionaries_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Key).HasColumnName("key").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.IsSystem).HasColumnName("is_system");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ManagerVisibility).HasColumnName("manager_visibility");
            e.Property(x => x.EntrySortMode).HasColumnName("entry_sort_mode");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<DictionaryEntry>(e =>
        {
            e.ToTable("dictionary_entries");
            e.HasKey(x => x.Id).HasName("dictionary_entries_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DictionaryId).HasColumnName("dictionary_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.NormalizedValue).HasColumnName("normalized_value").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.Icon).HasColumnName("icon");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        DictionariesRoutes.Map(routes);
        DictionaryEntriesRoutes.Map(routes);
    }
}
