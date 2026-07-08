namespace OpenMercato.Modules.QueryIndex.Data;

/// <summary>
/// The generic JSONB projection row (upstream <c>entity_indexes</c>, data/entities.ts). One row per
/// indexed record per org bucket; <see cref="Doc"/> holds the flattened base columns + <c>cf:&lt;key&gt;</c>
/// custom-field values + <c>search_text</c>. This is what list endpoints filter/sort/paginate against
/// instead of touching base tables + EAV joins (spec 03 R49). The <c>organization_id_coalesced</c>
/// generated column (upsert conflict target) lives only in the raw-SQL migration — not mapped here.
/// </summary>
public sealed class EntityIndexRow
{
    public Guid Id { get; set; }
    /// <summary>Entity identifier <c>'&lt;module&gt;:&lt;entity&gt;'</c>.</summary>
    public string EntityType { get; set; } = string.Empty;
    /// <summary>Record id as text (uuid/int compatible).</summary>
    public string EntityId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    /// <summary>Flattened document (base + <c>cf:&lt;key&gt;</c> + <c>search_text</c>) as jsonb.</summary>
    public string Doc { get; set; } = "{}";
    /// <summary>Optional embedding/metadata from secondary indexers (jsonb).</summary>
    public string? Embedding { get; set; }
    public int IndexVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Reindex/purge job progress per entity + scope (upstream <c>entity_index_jobs</c>).</summary>
public sealed class EntityIndexJob
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public int? PartitionIndex { get; set; }
    public int? PartitionCount { get; set; }
    public int? ProcessedCount { get; set; }
    public int? TotalCount { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    /// <summary><c>reindexing</c> | <c>purging</c>.</summary>
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Base-vs-indexed count snapshot per scope (upstream <c>entity_index_coverage</c>).</summary>
public sealed class EntityIndexCoverage
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public bool WithDeleted { get; set; }
    public int BaseCount { get; set; }
    public int IndexedCount { get; set; }
    public int VectorIndexedCount { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}

/// <summary>Indexer error log (upstream <c>indexer_error_logs</c>). "Index errors never fail the write".</summary>
public sealed class IndexerErrorLog
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Handler { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? RecordId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string? Payload { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Indexer status log (upstream <c>indexer_status_logs</c>).</summary>
public sealed class IndexerStatusLog
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Handler { get; set; } = string.Empty;
    public string Level { get; set; } = "info";
    public string? EntityType { get; set; }
    public string? RecordId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Inverted token index for search (upstream <c>search_tokens</c>). PARITY-TODO in this port.</summary>
public sealed class SearchToken
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public Guid? OrganizationId { get; set; }
    public Guid? TenantId { get; set; }
    public string Field { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string? Token { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
