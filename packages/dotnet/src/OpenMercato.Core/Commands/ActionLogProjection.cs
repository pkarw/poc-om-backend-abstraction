using System.Text.Json;

namespace OpenMercato.Core.Commands;

/// <summary>
/// Pure projection helpers for action-log rows (port of upstream <c>projections.ts</c>
/// <c>deriveActionLogActionType</c> + <c>deriveActionLogChangedFields</c>). Turns before/after snapshots
/// into a <c>{ field: { from, to } }</c> change map and derives the action verb from the command id so the
/// changelog UI + CSV export show field-level changes and the <c>action_type</c>/<c>changed_fields</c>
/// columns are populated for filtering.
/// </summary>
public static class ActionLogProjection
{
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "create", "update", "edit", "delete", "assign" };
    // Bookkeeping columns that always change / are not user-meaningful — never surface them as changes.
    private static readonly HashSet<string> IgnoredFields = new(StringComparer.OrdinalIgnoreCase) { "updatedAt", "createdAt", "id" };

    /// <summary>Diff two serialized snapshot JSON objects into an ordered <c>field → {from,to}</c> map of changes.</summary>
    public static IReadOnlyDictionary<string, object?> DiffSnapshots(string beforeJson, string afterJson)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        JsonElement before, after;
        try { before = JsonSerializer.Deserialize<JsonElement>(beforeJson); after = JsonSerializer.Deserialize<JsonElement>(afterJson); }
        catch { return result; }
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object) return result;

        // Union of keys, preserving the "after" order first (new fields), then before-only removals.
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in after.EnumerateObject()) if (seen.Add(p.Name)) keys.Add(p.Name);
        foreach (var p in before.EnumerateObject()) if (seen.Add(p.Name)) keys.Add(p.Name);

        foreach (var key in keys)
        {
            if (IgnoredFields.Contains(key)) continue;
            var hasB = before.TryGetProperty(key, out var b);
            var hasA = after.TryGetProperty(key, out var a);
            var bText = hasB ? b.GetRawText() : "null";
            var aText = hasA ? a.GetRawText() : "null";
            if (bText == aText) continue; // unchanged
            result[key] = new
            {
                from = hasB ? ToClr(b) : null,
                to = hasA ? ToClr(a) : null,
            };
        }
        return result;
    }

    /// <summary>Derive the action verb from the command id trailing segment (<c>customers.people.update</c> → update).</summary>
    public static string? DeriveActionType(string? commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return null;
        var last = commandId.Split('.').LastOrDefault();
        if (last is not null && Verbs.Contains(last)) return last == "edit" ? "update" : last;
        // Fall back to a keyword scan (e.g. "...deleteMany").
        foreach (var v in Verbs)
            if (commandId.Contains(v, StringComparison.OrdinalIgnoreCase)) return v == "edit" ? "update" : v;
        return null;
    }

    private static object? ToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        _ => el.GetRawText(),
    };
}
