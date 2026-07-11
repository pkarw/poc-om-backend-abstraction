using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.QueryIndex.Api;
using OpenMercato.Modules.QueryIndex.Crud;
using OpenMercato.Modules.QueryIndex.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex;

/// <summary>
/// The query_index module (upstream packages/core/src/modules/query_index) — the hybrid query layer.
/// Owns the 6 read-model tables (entity_indexes projection, entity_index_jobs, entity_index_coverage,
/// indexer_error_logs, indexer_status_logs, search_tokens; byte-exact DDL in the raw-SQL migration
/// <c>20260707060000_AddQueryIndexModule</c>) and implements the CRUD-factory read/write index seams:
///   - <see cref="ICrudIndexer"/> (<see cref="QueryIndexCrudIndexer"/>): builds/updates the
///     <c>entity_indexes</c> doc on every create/update/delete (last-wins over Core's no-op).
///   - <see cref="ICrudIndexQuery"/> (<see cref="QueryIndexCrudListQuery"/>): resolves index-backed list
///     pages (filter/sort by base fields + <c>cf:&lt;key&gt;</c>) for opt-in CRUD lists.
///   - <see cref="IQueryIndexEngine"/>: the public query API a module's list can call directly.
///
/// PARITY-TODO: partitioned reindex/purge worker jobs + heartbeat/progress, coverage snapshots + warmup,
/// search_tokens token search, index-doc encryption, and vector/fulltext status columns are clean seams.
/// </summary>
public sealed class QueryIndexModule : IModule
{
    public string Id => "query_index";

    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "query_index.status.view",
        "query_index.reindex",
        "query_index.purge",
    };

    /// <summary>The 3 ACL features with titles (upstream acl.ts / lib/events.ts features export).</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("query_index.status.view", "View index status"),
        new AclFeatureDefinition("query_index.reindex", "Trigger reindex"),
        new AclFeatureDefinition("query_index.purge", "Purge index"),
    };

    /// <summary>Admin gets the whole feature family (upstream setup.ts <c>admin: ['query_index.*']</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>> { ["admin"] = new[] { "query_index.*" } };

    public void ConfigureServices(IServiceCollection services)
    {
        // Real index seams — override Core's no-ops (last registration wins).
        services.AddScoped<ICrudIndexer, QueryIndexCrudIndexer>();
        services.AddScoped<ICrudIndexQuery, QueryIndexCrudListQuery>();
        services.AddScoped<IEntityLookupQuery, QueryIndexEntityLookupQuery>();
        // Public query engine + base-row resolver.
        services.AddScoped<IQueryIndexEngine, QueryIndexEngine>();
        services.AddScoped<IIndexBaseRowResolver, CustomEntitiesStorageBaseRowResolver>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntityIndexRow>(e =>
        {
            e.ToTable("entity_indexes");
            e.HasKey(x => x.Id).HasName("entity_indexes_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Doc).HasColumnName("doc").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Embedding).HasColumnName("embedding").HasColumnType("jsonb");
            e.Property(x => x.IndexVersion).HasColumnName("index_version");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<EntityIndexJob>(e =>
        {
            e.ToTable("entity_index_jobs");
            e.HasKey(x => x.Id).HasName("entity_index_jobs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PartitionIndex).HasColumnName("partition_index");
            e.Property(x => x.PartitionCount).HasColumnName("partition_count");
            e.Property(x => x.ProcessedCount).HasColumnName("processed_count");
            e.Property(x => x.TotalCount).HasColumnName("total_count");
            e.Property(x => x.HeartbeatAt).HasColumnName("heartbeat_at").HasColumnType("timestamptz");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<EntityIndexCoverage>(e =>
        {
            e.ToTable("entity_index_coverage");
            e.HasKey(x => x.Id).HasName("entity_index_coverage_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.WithDeleted).HasColumnName("with_deleted");
            e.Property(x => x.BaseCount).HasColumnName("base_count");
            e.Property(x => x.IndexedCount).HasColumnName("indexed_count");
            e.Property(x => x.VectorIndexedCount).HasColumnName("vector_indexed_count");
            e.Property(x => x.RefreshedAt).HasColumnName("refreshed_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<IndexerErrorLog>(e =>
        {
            e.ToTable("indexer_error_logs");
            e.HasKey(x => x.Id).HasName("indexer_error_logs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.Property(x => x.Handler).HasColumnName("handler").IsRequired();
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.RecordId).HasColumnName("record_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.Message).HasColumnName("message").IsRequired();
            e.Property(x => x.Stack).HasColumnName("stack");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<IndexerStatusLog>(e =>
        {
            e.ToTable("indexer_status_logs");
            e.HasKey(x => x.Id).HasName("indexer_status_logs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.Property(x => x.Handler).HasColumnName("handler").IsRequired();
            e.Property(x => x.Level).HasColumnName("level").IsRequired();
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.RecordId).HasColumnName("record_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Message).HasColumnName("message").IsRequired();
            e.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<SearchToken>(e =>
        {
            e.ToTable("search_tokens");
            e.HasKey(x => x.Id).HasName("search_tokens_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Field).HasColumnName("field").IsRequired();
            e.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired();
            e.Property(x => x.Token).HasColumnName("token");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });
    }

    /// <summary>CLI subcommands (upstream cli.ts): <c>query_index reindex</c>.</summary>
    public IReadOnlyList<ICliCommand> CliCommands { get; } = new ICliCommand[]
    {
        new Cli.ReindexCommand(),
    };

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        QueryIndexRoutes.Map(routes);
    }
}
