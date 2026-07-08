namespace OpenMercato.Modules.Entities.Data;

/// <summary>
/// Custom-field DEFINITION (upstream <c>custom_field_defs</c>, data/entities.ts). Scoped to an entity
/// id (<c>'&lt;module&gt;:&lt;entity&gt;'</c>) and optional tenant/org (null = global). <see cref="ConfigJson"/>
/// holds the UI/behaviour metadata (label, options, validation rules, multi, encrypted, …) as jsonb.
/// </summary>
public sealed class CustomFieldDef
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    /// <summary>Raw jsonb config (label/options/validation/multi/encrypted/…). Null when none.</summary>
    public string? ConfigJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Per-entity fieldset configuration (upstream <c>custom_field_entity_configs</c>).</summary>
public sealed class CustomFieldEntityConfig
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public string? ConfigJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>User-defined logical entity registry (upstream <c>custom_entities</c>).</summary>
public sealed class CustomEntity
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? LabelField { get; set; }
    public string? DefaultEditor { get; set; }
    public bool ShowInSidebar { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>JSONB document store for custom-entity records (upstream <c>custom_entities_storage</c>).</summary>
public sealed class CustomEntityStorage
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public string Doc { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// Custom-field VALUE (EAV) (upstream <c>custom_field_values</c>). One row per (entity_id, record_id,
/// field_key, scope); one typed value column is populated by kind. <see cref="RecordId"/> is text to
/// support any PK shape (uuid/int).
/// </summary>
public sealed class CustomFieldValue
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string? ValueText { get; set; }
    public string? ValueMultiline { get; set; }
    public int? ValueInt { get; set; }
    public float? ValueFloat { get; set; }
    public bool? ValueBool { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Per-entity field-encryption map (upstream <c>encryption_maps</c>).</summary>
public sealed class EncryptionMap
{
    public Guid Id { get; set; }
    public string EntityId { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string? FieldsJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
