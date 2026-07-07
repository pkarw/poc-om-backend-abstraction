using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMercato.Modules.Dashboards.Lib;

/// <summary>
/// Layout-item helpers — the .NET analogue of the layout route's <c>normalizeLayoutItems</c>. Layout
/// items are stored as a jsonb array of objects <c>{id,widgetId,order,priority?,size?,settings?}</c>;
/// this preserves arbitrary <c>settings</c> payloads via JsonNode.
/// </summary>
internal static class LayoutJson
{
    public static JsonArray Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonArray();
        try
        {
            var node = JsonNode.Parse(json);
            return node as JsonArray ?? new JsonArray();
        }
        catch { return new JsonArray(); }
    }

    public static string Serialize(IEnumerable<JsonObject> items)
    {
        var arr = new JsonArray();
        foreach (var item in items) arr.Add(item.DeepClone());
        return arr.ToJsonString();
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? AsInt(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            // Accept integral numbers only (upstream Number.isInteger check).
            if (v.TryGetValue<long>(out var l) && l >= int.MinValue && l <= int.MaxValue)
            {
                // Reject non-integral doubles that happen to round-trip through long.
                if (v.TryGetValue<double>(out var d) && d != Math.Floor(d)) return null;
                return (int)l;
            }
            if (v.TryGetValue<int>(out var i)) return i;
        }
        return null;
    }

    /// <summary>
    /// Dedupe by id, coerce order/priority, sort by <c>order ?? priority ?? 0</c>, then re-index
    /// densely (order = priority = index). Items missing a non-empty id or widgetId are dropped.
    /// </summary>
    public static List<JsonObject> Normalize(JsonArray raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sanitized = new List<(JsonObject Obj, int SortKey)>();

        foreach (var element in raw)
        {
            if (element is not JsonObject item) continue;
            var id = AsString(item["id"]);
            var widgetId = AsString(item["widgetId"]);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(widgetId)) continue;
            if (!seen.Add(id)) continue;

            var order = AsInt(item["order"]);
            var priority = AsInt(item["priority"]);
            var size = AsString(item["size"]);

            var obj = new JsonObject { ["id"] = id, ["widgetId"] = widgetId };
            if (size is not null) obj["size"] = size;
            if (item.TryGetPropertyValue("settings", out var settings))
                obj["settings"] = settings?.DeepClone();

            sanitized.Add((obj, order ?? priority ?? 0));
        }

        return sanitized
            .OrderBy(x => x.SortKey)
            .Select((x, idx) =>
            {
                x.Obj["order"] = idx;
                x.Obj["priority"] = idx;
                return x.Obj;
            })
            .ToList();
    }

    /// <summary>The widgetId of a stored item, or null.</summary>
    public static string? WidgetId(JsonObject item) => AsString(item["widgetId"]);

    /// <summary>The id of a stored item, or null.</summary>
    public static string? ItemId(JsonObject item) => AsString(item["id"]);
}
