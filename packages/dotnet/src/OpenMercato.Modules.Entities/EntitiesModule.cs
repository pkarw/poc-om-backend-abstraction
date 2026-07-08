using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities.Api;
using OpenMercato.Modules.Entities.Crud;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities;

/// <summary>
/// The entities module (upstream packages/core/src/modules/entities) — the custom-field engine. Owns
/// the 6 EAV tables (custom_field_defs, custom_field_entity_configs, custom_entities,
/// custom_entities_storage, custom_field_values, encryption_maps; byte-exact DDL in the raw-SQL
/// migration <c>20260707050000_AddEntitiesModule</c>) and implements the two CRUD-factory seams:
///   - <see cref="ICrudCustomFields"/> (custom-field wire codec): registered in
///     <see cref="ConfigureServices"/> AFTER Core's no-op, so last-wins gives the factory the real codec.
///   - install-from-CE: <see cref="Lib.InstallFromCe"/> materializes every module's declared field sets
///     into <c>custom_field_defs</c> rows (invoked by the <c>entities install-ce</c> CLI command and at
///     tenant seed time).
///
/// PARITY-TODO: field-value encryption (encryption_maps + DEKs), the definitions cache, the
/// query-index-backed records listing/export, dictionaries/currency default-value validation, and the
/// batch/restore/encryption/sidebar admin routes are clean seams deferred to later ports.
/// </summary>
public sealed class EntitiesModule : IModule
{
    public string Id => "entities";

    /// <summary>The 4 ACL feature ids (acl.ts). Kept for back-compat.</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "entities.definitions.view",
        "entities.definitions.manage",
        "entities.records.view",
        "entities.records.manage",
    };

    /// <summary>The 4 ACL features with their exact titles (upstream acl.ts, all module 'entities').</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("entities.definitions.view", "View custom field definitions"),
        new AclFeatureDefinition("entities.definitions.manage", "Manage custom field definitions"),
        new AclFeatureDefinition("entities.records.view", "View records"),
        new AclFeatureDefinition("entities.records.manage", "Manage records"),
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // Real custom-field wire codec — overrides Core's NoopCrudCustomFields (last registration wins).
        services.AddScoped<ICrudCustomFields, EntitiesCrudCustomFields>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomFieldDef>(e =>
        {
            e.ToTable("custom_field_defs");
            e.HasKey(x => x.Id).HasName("custom_field_defs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Key).HasColumnName("key").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomFieldEntityConfig>(e =>
        {
            e.ToTable("custom_field_entity_configs");
            e.HasKey(x => x.Id).HasName("custom_field_entity_configs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ConfigJson).HasColumnName("config_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomEntity>(e =>
        {
            e.ToTable("custom_entities");
            e.HasKey(x => x.Id).HasName("custom_entities_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.LabelField).HasColumnName("label_field");
            e.Property(x => x.DefaultEditor).HasColumnName("default_editor");
            e.Property(x => x.ShowInSidebar).HasColumnName("show_in_sidebar");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomEntityStorage>(e =>
        {
            e.ToTable("custom_entities_storage");
            e.HasKey(x => x.Id).HasName("custom_entities_storage_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Doc).HasColumnName("doc").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomFieldValue>(e =>
        {
            e.ToTable("custom_field_values");
            e.HasKey(x => x.Id).HasName("custom_field_values_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.RecordId).HasColumnName("record_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.FieldKey).HasColumnName("field_key").IsRequired();
            e.Property(x => x.ValueText).HasColumnName("value_text");
            e.Property(x => x.ValueMultiline).HasColumnName("value_multiline");
            e.Property(x => x.ValueInt).HasColumnName("value_int");
            e.Property(x => x.ValueFloat).HasColumnName("value_float").HasColumnType("real");
            e.Property(x => x.ValueBool).HasColumnName("value_bool");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<EncryptionMap>(e =>
        {
            e.ToTable("encryption_maps");
            e.HasKey(x => x.Id).HasName("encryption_maps_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.FieldsJson).HasColumnName("fields_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }

    /// <summary>CLI subcommands (upstream cli.ts): <c>entities install-ce</c>.</summary>
    public IReadOnlyList<ICliCommand> CliCommands { get; } = new ICliCommand[]
    {
        new Cli.InstallCeCommand(),
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        DefinitionsRoutes.Map(routes);
        RecordsRoutes.Map(routes);
    }
}
