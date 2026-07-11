using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.QueryIndex.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex.Crud;

/// <summary>
/// The real <see cref="IEntityLookupQuery"/> — returns whole doc rows from the <c>entity_indexes</c> read
/// model so the entities <c>relations/options</c> endpoint can populate relation-kind pickers (id + label +
/// route-context fields). The .NET stand-in for upstream's generic <c>queryEngine.query</c> doc read (OM
/// hits base tables; here the projected index doc carries the same base + <c>cf:&lt;key&gt;</c> fields).
/// </summary>
public sealed class QueryIndexEntityLookupQuery : IEntityLookupQuery
{
    private readonly AppDbContext _db;
    public QueryIndexEntityLookupQuery(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LookupAsync(EntityLookupRequest req, CancellationToken ct = default)
    {
        var rows = await ScopedRows(req.EntityType, req.TenantId, req.OrganizationIds).ToListAsync(ct);

        // id restriction (scalar or set).
        if (req.Ids.Count > 0)
        {
            var idSet = new HashSet<string>(req.Ids, StringComparer.Ordinal);
            rows = rows.Where(r => idSet.Contains(r.EntityId)).ToList();
        }

        var docs = rows.Select(r => (Row: r, Doc: DocJson.ParseObject(r.Doc))).ToList();

        // q substring filter on the label field (base key or cf:<key>), case-insensitive.
        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var term = req.Q.Trim();
            docs = docs.Where(d => (LabelValue(d.Doc, req.LabelField) ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var page = docs.Take(Math.Max(1, req.PageSize)).ToList();

        var result = new List<IReadOnlyDictionary<string, object?>>(page.Count);
        foreach (var (row, doc) in page)
        {
            var projected = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = row.EntityId };
            foreach (var f in req.Fields)
            {
                if (f == "id") continue;
                projected[f] = FieldValue(doc, f);
            }
            result.Add(projected);
        }
        return result;
    }

    public async Task<bool> FieldExistsAsync(string entityType, string field, Guid? tenantId, IReadOnlyList<Guid>? organizationIds, CancellationToken ct = default)
    {
        // Probe: any in-scope indexed doc that carries the field (base key or cf:<key>).
        var docs = await ScopedRows(entityType, tenantId, organizationIds).Select(r => r.Doc).Take(200).ToListAsync(ct);
        foreach (var raw in docs)
        {
            var doc = DocJson.ParseObject(raw);
            if (doc.ContainsKey(field) || doc.ContainsKey("cf:" + field)) return true;
        }
        return false;
    }

    private IQueryable<EntityIndexRow> ScopedRows(string entityType, Guid? tenantId, IReadOnlyList<Guid>? orgIds)
    {
        var q = _db.Set<EntityIndexRow>().AsNoTracking()
            .Where(r => r.EntityType == entityType && r.DeletedAt == null && r.TenantId == tenantId);
        if (orgIds is { Count: > 0 })
            q = q.Where(r => r.OrganizationId == null || orgIds.Contains(r.OrganizationId.Value));
        return q;
    }

    // Base columns live in the doc under their raw key; custom fields under cf:<key> (IndexDocument).
    private static object? FieldValue(IReadOnlyDictionary<string, object?> doc, string field) =>
        doc.TryGetValue(field, out var v) ? v : doc.TryGetValue("cf:" + field, out var cv) ? cv : null;

    private static string? LabelValue(IReadOnlyDictionary<string, object?> doc, string labelField) =>
        DocJson.ToText(FieldValue(doc, labelField));
}
