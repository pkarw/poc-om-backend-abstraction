namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>A single scoped custom-field value contributing to a doc (upstream <c>IndexCustomFieldValue</c>).</summary>
public sealed record IndexCustomFieldValue(string Key, object? Value, Guid? OrganizationId, Guid? TenantId);

/// <summary>
/// The pure document projection — the port of upstream <c>lib/document.ts</c>
/// (<c>buildIndexDocument</c> / <c>attachAggregateSearchField</c>). Merges a base row and the visible
/// custom-field values into a single flat doc: base keys verbatim, custom fields under
/// <c>cf:&lt;key&gt;</c> (singleton value or array), plus the aggregate <c>search_text</c> field.
/// </summary>
public static class IndexDocument
{
    public const string AggregateSearchField = "search_text";

    /// <summary>
    /// Build the flattened doc from a base row + custom-field values, gated by the record's scope.
    /// A field row is visible when its org/tenant is null or equals the scope (upstream
    /// <c>isScopedValueVisible</c>). Values are collapsed to a scalar (one) or array (many).
    /// </summary>
    public static Dictionary<string, object?> Build(
        IReadOnlyDictionary<string, object?> baseRow,
        IEnumerable<IndexCustomFieldValue> customFieldValues,
        Guid? scopeOrganizationId,
        Guid? scopeTenantId)
    {
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in baseRow) doc[key] = value;

        var grouped = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var field in customFieldValues)
        {
            if (!IsScopedValueVisible(scopeOrganizationId, field.OrganizationId)) continue;
            if (!IsScopedValueVisible(scopeTenantId, field.TenantId)) continue;

            var bucketKey = "cf:" + field.Key;
            if (!grouped.TryGetValue(bucketKey, out var bucket))
            {
                bucket = new List<object?>();
                grouped[bucketKey] = bucket;
                order.Add(bucketKey);
            }

            if (field.Value is System.Collections.IEnumerable arr and not string)
                foreach (var entry in arr) bucket.Add(NormalizeValue(entry));
            else
                bucket.Add(NormalizeValue(field.Value));
        }

        foreach (var key in order)
        {
            var values = grouped[key];
            if (values.Count == 1) doc[key] = values[0];
            else if (values.Count > 1) doc[key] = values;
        }

        return AttachAggregateSearchField(doc);
    }

    /// <summary>Attach <c>search_text</c> = newline-joined, case-insensitively deduped string values.</summary>
    public static Dictionary<string, object?> AttachAggregateSearchField(Dictionary<string, object?> doc)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (field, value) in doc)
        {
            foreach (var entry in CollectAggregateSearchValues(field, value))
            {
                var key = entry.ToLowerInvariant();
                if (!seen.Add(key)) continue;
                parts.Add(entry);
            }
        }

        if (parts.Count > 0) doc[AggregateSearchField] = string.Join("\n", parts);
        return doc;
    }

    private static IEnumerable<string> CollectAggregateSearchValues(string field, object? value)
    {
        var lower = field.ToLowerInvariant();
        if (lower == AggregateSearchField
            || lower == "id"
            || lower.EndsWith("_id", StringComparison.Ordinal)
            || lower.EndsWith(".id", StringComparison.Ordinal)
            || lower.EndsWith("_at", StringComparison.Ordinal)
            || lower is "created_at" or "updated_at" or "deleted_at" or "tenant_id" or "organization_id")
            yield break;

        if (value is string s)
        {
            var trimmed = s.Trim();
            if (trimmed.Length > 0) yield return trimmed;
            yield break;
        }

        if (value is System.Collections.IEnumerable arr and not string)
        {
            foreach (var entry in arr)
                if (entry is string es)
                {
                    var t = es.Trim();
                    if (t.Length > 0) yield return t;
                }
        }
    }

    private static bool IsScopedValueVisible(Guid? scopeValue, Guid? fieldValue)
    {
        // Upstream document.ts::isScopedValueVisible: a null scope only sees null field rows;
        // a concrete scope sees null (global) or exactly-matching field rows.
        if (scopeValue is null) return fieldValue is null;
        return fieldValue is null || fieldValue == scopeValue;
    }

    private static object? NormalizeValue(object? value) => value; // JS undefined→null already null in CLR
}
