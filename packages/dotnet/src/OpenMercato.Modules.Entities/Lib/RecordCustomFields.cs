using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>
/// The EAV value read/write engine — the port of <c>lib/helpers.ts::setRecordCustomFields</c> (write)
/// and <c>shared/lib/crud/custom-fields.ts::loadCustomFieldValues</c> (read). Chooses the storage
/// column from the resolved definition's kind (falling back to JS-value shape when undeclared), clears
/// all value columns on update to avoid leftovers, and replaces all rows for multi-value keys.
///
/// PARITY-TODO: field-value encryption (encryption_maps + tenant DEKs) is a clean seam here — the
/// upstream <c>encrypted</c> path is not reproduced (see ADR / spec 03).
/// </summary>
public static class RecordCustomFields
{
    /// <summary>Upsert the custom-field values for one record (bare-key values).</summary>
    public static async Task SetAsync(
        AppDbContext db,
        string entityId,
        string recordId,
        Guid? tenantId,
        Guid? organizationId,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken ct = default)
    {
        var defsByKey = await CustomFieldDefsService.LoadWinningDefsAsync(db, entityId, tenantId, organizationId, ct);

        foreach (var (fieldKey, raw) in values)
        {
            defsByKey.TryGetValue(fieldKey, out var def);

            if (raw is System.Collections.IEnumerable arr and not string)
            {
                // Multi-value: replace all existing rows for the key.
                var stale = await db.Set<CustomFieldValue>()
                    .Where(v => v.EntityId == entityId && v.RecordId == recordId
                                && v.TenantId == tenantId && v.OrganizationId == organizationId
                                && v.FieldKey == fieldKey)
                    .ToListAsync(ct);
                db.Set<CustomFieldValue>().RemoveRange(stale);

                foreach (var item in arr)
                {
                    var col = def is not null ? CustomFieldKinds.ColumnFromKind(def.Kind) : ColumnFromValue(item);
                    var row = NewRow(entityId, recordId, tenantId, organizationId, fieldKey);
                    Assign(row, col, item);
                    db.Set<CustomFieldValue>().Add(row);
                }
                continue;
            }

            var column = def is not null ? CustomFieldKinds.ColumnFromKind(def.Kind) : ColumnFromValue(raw);
            var existing = await db.Set<CustomFieldValue>()
                .FirstOrDefaultAsync(v => v.EntityId == entityId && v.RecordId == recordId
                                          && v.TenantId == tenantId && v.OrganizationId == organizationId
                                          && v.FieldKey == fieldKey, ct);
            if (existing is null)
            {
                existing = NewRow(entityId, recordId, tenantId, organizationId, fieldKey);
                db.Set<CustomFieldValue>().Add(existing);
            }
            ClearColumns(existing);
            Assign(existing, column, raw);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Load bare-key custom-field values for a set of records (batch, N+1-safe).</summary>
    public static async Task<Dictionary<string, Dictionary<string, object?>>> LoadAsync(
        AppDbContext db,
        string entityId,
        IReadOnlyList<string> recordIds,
        Guid? tenantId,
        Guid? organizationId,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        if (recordIds.Count == 0) return result;

        var rows = await db.Set<CustomFieldValue>()
            .Where(v => v.EntityId == entityId && recordIds.Contains(v.RecordId) && v.DeletedAt == null)
            .Where(v => v.TenantId == null || v.TenantId == tenantId)
            .ToListAsync(ct);
        if (rows.Count == 0) return result;

        var defsByKey = await CustomFieldDefsService.LoadWinningDefsAsync(db, entityId, tenantId, organizationId, ct);

        // (recordId, fieldKey) → collected values (preserve insertion order).
        var buckets = new Dictionary<(string, string), List<object?>>();
        foreach (var row in rows)
        {
            var bucketKey = (row.RecordId, row.FieldKey);
            if (!buckets.TryGetValue(bucketKey, out var list)) { list = new List<object?>(); buckets[bucketKey] = list; }
            list.Add(ValueFromRow(row));
        }

        foreach (var ((recordId, fieldKey), list) in buckets)
        {
            if (!result.TryGetValue(recordId, out var map)) { map = new Dictionary<string, object?>(StringComparer.Ordinal); result[recordId] = map; }
            var isMulti = defsByKey.TryGetValue(fieldKey, out var def) && CustomFieldDefsService.IsMulti(def);
            if (isMulti)
                map[fieldKey] = list.Where(v => v is not null).ToList();
            else if (list.Count > 1)
                map[fieldKey] = list;
            else
                map[fieldKey] = list.Count > 0 ? list[0] : null;
        }
        return result;
    }

    private static object? ValueFromRow(CustomFieldValue row)
    {
        if (row.ValueMultiline is not null) return row.ValueMultiline;
        if (row.ValueText is not null) return row.ValueText;
        if (row.ValueInt is not null) return row.ValueInt;
        if (row.ValueFloat is not null) return row.ValueFloat;
        if (row.ValueBool is not null) return row.ValueBool;
        return null;
    }

    private static CustomFieldValue NewRow(string entityId, string recordId, Guid? tenantId, Guid? orgId, string fieldKey) => new()
    {
        Id = Guid.NewGuid(),
        EntityId = entityId,
        RecordId = recordId,
        TenantId = tenantId,
        OrganizationId = orgId,
        FieldKey = fieldKey,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static void ClearColumns(CustomFieldValue cf)
    {
        cf.ValueText = null;
        cf.ValueMultiline = null;
        cf.ValueInt = null;
        cf.ValueFloat = null;
        cf.ValueBool = null;
    }

    private static ValueColumn ColumnFromValue(object? v) => v switch
    {
        null => ValueColumn.Text,
        bool => ValueColumn.Bool,
        long l => ValueColumn.Int,
        int => ValueColumn.Int,
        double or float => ValueColumn.Float,
        _ => ValueColumn.Text,
    };

    private static void Assign(CustomFieldValue cf, ValueColumn column, object? value)
    {
        switch (column)
        {
            case ValueColumn.Text: cf.ValueText = value?.ToString(); break;
            case ValueColumn.Multiline: cf.ValueMultiline = value?.ToString(); break;
            case ValueColumn.Int: cf.ValueInt = value is null ? null : Convert.ToInt32(value); break;
            case ValueColumn.Float: cf.ValueFloat = value is null ? null : Convert.ToSingle(value); break;
            case ValueColumn.Bool: cf.ValueBool = value is null ? null : Convert.ToBoolean(value); break;
        }
    }
}
