using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.QueryIndex.Data;

namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>
/// Best-effort reindex/purge over the storage-backed records — the synchronous .NET stand-in for
/// upstream <c>lib/reindexer.ts</c> + <c>subscribers/reindex.ts</c> / <c>purge.ts</c>. Shared by the
/// status/reindex/purge routes and the <c>query_index reindex</c> CLI command.
///
/// PARITY-TODO: partitioned worker jobs, <c>entity_index_jobs</c> heartbeat/progress, coverage refresh,
/// and vector/fulltext reindex are seams — this walks the base records and re-projects each via the
/// <see cref="ICrudIndexer"/>.
/// </summary>
public static class Reindexer
{
    private static readonly Regex EntityIdShape = new("^[a-z0-9_]+:[a-z0-9_]+$", RegexOptions.Compiled);

    public static bool IsValidEntityIdShape(string? entityType)
        => !string.IsNullOrWhiteSpace(entityType) && EntityIdShape.IsMatch(entityType);

    /// <summary>
    /// Re-project every BASE record of an entity type (in scope) into <c>entity_indexes</c>. The record
    /// source is the <see cref="IIndexBaseRowResolver"/> chain (<see cref="IIndexBaseRowResolver.EnumerateRecordIdsAsync"/>),
    /// so module-owned entities (customers people/companies/deals in their own tables) are walked, not just
    /// <c>custom_entities_storage</c> — the previous storage-only enumeration silently reindexed 0 rows for
    /// those (the "full reindex does nothing" bug).
    /// </summary>
    public static async Task<int> ReindexEntityAsync(
        AppDbContext db, ICrudIndexer indexer, string entityType, Guid? tenantId,
        IIndexBaseRowResolver? baseRows = null, CancellationToken ct = default)
    {
        IReadOnlyList<(string RecordId, Guid? OrganizationId, Guid? TenantId)>? records =
            baseRows is null ? null : await baseRows.EnumerateRecordIdsAsync(entityType, tenantId, ct);

        // Fallback (no resolver / resolver disclaims the type): the generic storage source.
        records ??= (await db.Set<CustomEntityStorage>().AsNoTracking()
                .Where(s => s.EntityType == entityType && s.DeletedAt == null)
                .Where(s => tenantId == null || s.TenantId == null || s.TenantId == tenantId)
                .Select(s => new { s.EntityId, s.OrganizationId, s.TenantId })
                .ToListAsync(ct))
            .Select(s => (s.EntityId, s.OrganizationId, s.TenantId)).ToList();

        var count = 0;
        foreach (var r in records)
        {
            await indexer.UpsertOneAsync(entityType, r.RecordId, r.OrganizationId, r.TenantId, "reindex", ct);
            count++;
        }
        return count;
    }

    /// <summary>Remove all <c>entity_indexes</c> rows for an entity type within the tenant/org scope.</summary>
    public static async Task<int> PurgeEntityAsync(
        AppDbContext db, string entityType, Guid? tenantId, IReadOnlyList<Guid>? organizationIds, CancellationToken ct = default)
    {
        var q = db.Set<EntityIndexRow>().Where(r => r.EntityType == entityType);
        q = q.Where(r => r.TenantId == tenantId);
        if (organizationIds is { Count: > 0 })
            q = q.Where(r => r.OrganizationId == null || organizationIds.Contains(r.OrganizationId.Value));

        var rows = await q.ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.Set<EntityIndexRow>().RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
