using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>
/// Resolves the BASE row for a record being indexed — the port of upstream
/// <c>resolveEntityTableName</c> + <c>select * from &lt;baseTable&gt; where id = recordId</c>
/// (indexer.ts::buildIndexDoc). Returns the base fields (snake_case keys) as a dict, or <c>null</c> when
/// the record no longer exists (→ the index row is removed, matching upstream's <c>doc == null</c> path).
///
/// PARITY-TODO: the default resolver reads the entities module's <c>custom_entities_storage</c> doc
/// store (the generic record backing for user-defined + doc-backed entities). System entities that own
/// their own tables (customers, sales, …) register their own resolver when ported.
/// </summary>
public interface IIndexBaseRowResolver
{
    Task<IReadOnlyDictionary<string, object?>?> LoadAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default);
}

/// <summary>Default resolver reading <c>custom_entities_storage.doc</c> for the (entity_type, entity_id) record.</summary>
public sealed class CustomEntitiesStorageBaseRowResolver : IIndexBaseRowResolver
{
    private readonly AppDbContext _db;
    public CustomEntitiesStorageBaseRowResolver(AppDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, object?>?> LoadAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
    {
        var row = await _db.Set<CustomEntityStorage>().AsNoTracking()
            .Where(s => s.EntityType == entityType && s.EntityId == recordId && s.DeletedAt == null)
            .Where(s => s.TenantId == null || s.TenantId == tenantId)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var doc = DocJson.ParseObject(row.Doc);
        // Ensure the record id is present under the conventional 'id' key (upstream base rows carry it).
        if (!doc.ContainsKey("id")) doc["id"] = recordId;
        return doc;
    }
}
