using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Port of the shared sidebar-preferences settings shape
/// (<c>packages/shared/src/modules/navigation/sidebarPreferences.ts</c>). The settings object is
/// stored as raw jsonb in the <c>settings_json</c> columns and echoed verbatim in API responses.
/// Field-normalization rules mirror <c>normalizeSidebarSettings</c> byte-for-byte:
/// <list type="bullet">
/// <item><c>groupOrder</c>: string list kept as-is (no trim/dedupe).</item>
/// <item><c>groupLabels</c>/<c>itemLabels</c>: string→string map, values kept as-is (no trim).</item>
/// <item><c>hiddenItems</c>: trimmed, empties dropped, deduped (first wins).</item>
/// <item><c>itemOrder</c>: per key the list is trimmed/deduped/empty-dropped; empty lists drop the key.</item>
/// </list>
/// </summary>
public sealed class SidebarSettings
{
    public const int DefaultVersion = 2; // SIDEBAR_PREFERENCES_VERSION

    public int Version { get; set; } = DefaultVersion;
    public List<string> GroupOrder { get; set; } = new();
    public Dictionary<string, string> GroupLabels { get; set; } = new();
    public Dictionary<string, string> ItemLabels { get; set; } = new();
    public List<string> HiddenItems { get; set; } = new();
    public Dictionary<string, List<string>> ItemOrder { get; set; } = new();

    /// <summary>Empty/default settings (equivalent to <c>emptySettings()</c> / <c>normalizeSidebarSettings(null)</c>).</summary>
    public static SidebarSettings Default() => NormalizeCore(null, null, null, null, null, null);

    /// <summary>Exact port of <c>normalizeSidebarSettings</c>.</summary>
    public static SidebarSettings NormalizeCore(
        int? version,
        IEnumerable<string>? groupOrder,
        IEnumerable<KeyValuePair<string, string>>? groupLabels,
        IEnumerable<KeyValuePair<string, string>>? itemLabels,
        IEnumerable<string>? hiddenItems,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? itemOrder)
    {
        return new SidebarSettings
        {
            Version = version ?? DefaultVersion,
            // JS: Array.filter(typeof v === 'string') — no trim, no dedupe.
            GroupOrder = groupOrder is null ? new List<string>() : groupOrder.Where(v => v is not null).ToList(),
            GroupLabels = NormalizeRecord(groupLabels),
            ItemLabels = NormalizeRecord(itemLabels),
            HiddenItems = NormalizeStringArray(hiddenItems),
            ItemOrder = NormalizeStringArrayRecord(itemOrder),
        };
    }

    /// <summary>Normalize an already-materialized settings object (idempotent for stored values).</summary>
    public static SidebarSettings Normalize(SidebarSettings? s)
    {
        if (s is null) return Default();
        return NormalizeCore(
            s.Version,
            s.GroupOrder,
            s.GroupLabels,
            s.ItemLabels,
            s.HiddenItems,
            s.ItemOrder.Select(kv => new KeyValuePair<string, IEnumerable<string>>(kv.Key, kv.Value)));
    }

    /// <summary>Parse a stored <c>settings_json</c> string, tolerating null/garbage, then normalize.</summary>
    public static SidebarSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Default();

            int? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var iv)
                ? iv
                : null;

            return NormalizeCore(
                version,
                ReadStringArray(root, "groupOrder"),
                ReadStringRecord(root, "groupLabels"),
                ReadStringRecord(root, "itemLabels"),
                ReadStringArray(root, "hiddenItems"),
                ReadStringArrayRecord(root, "itemOrder"));
        }
        catch
        {
            return Default();
        }
    }

    /// <summary>The wire/storage object with exact camelCase keys and stable order.</summary>
    public IReadOnlyDictionary<string, object?> ToDict() => new Dictionary<string, object?>
    {
        ["version"] = Version,
        ["groupOrder"] = GroupOrder,
        ["groupLabels"] = GroupLabels,
        ["itemLabels"] = ItemLabels,
        ["hiddenItems"] = HiddenItems,
        ["itemOrder"] = ItemOrder,
    };

    /// <summary>Serialize to the jsonb string stored in <c>settings_json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(ToDict(), SidebarJson.Options);

    private static Dictionary<string, string> NormalizeRecord(IEnumerable<KeyValuePair<string, string>>? record)
    {
        var outMap = new Dictionary<string, string>();
        if (record is null) return outMap;
        foreach (var kv in record)
        {
            if (kv.Value is null) continue; // JS: keep only string values
            outMap[kv.Key] = kv.Value;
        }
        return outMap;
    }

    private static List<string> NormalizeStringArray(IEnumerable<string>? values)
    {
        var outList = new List<string>();
        if (values is null) return outList;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null) continue;
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            outList.Add(trimmed);
        }
        return outList;
    }

    private static Dictionary<string, List<string>> NormalizeStringArrayRecord(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? record)
    {
        var outMap = new Dictionary<string, List<string>>();
        if (record is null) return outMap;
        foreach (var kv in record)
        {
            var arr = NormalizeStringArray(kv.Value);
            if (arr.Count > 0) outMap[kv.Key] = arr;
        }
        return outMap;
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    private static List<KeyValuePair<string, string>> ReadStringRecord(JsonElement root, string name)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in el.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                list.Add(new KeyValuePair<string, string>(prop.Name, prop.Value.GetString()!));
        return list;
    }

    private static List<KeyValuePair<string, IEnumerable<string>>> ReadStringArrayRecord(JsonElement root, string name)
    {
        var list = new List<KeyValuePair<string, IEnumerable<string>>>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var items = new List<string>();
            foreach (var item in prop.Value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) items.Add(item.GetString()!);
            list.Add(new KeyValuePair<string, IEnumerable<string>>(prop.Name, items));
        }
        return list;
    }
}

/// <summary>Shared JSON options for the sidebar surface: no HTML escaping so response bytes match Node's JSON.stringify.</summary>
public static class SidebarJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Format like JS <c>Date.toISOString()</c> — always UTC with millisecond precision and a trailing Z.</summary>
    public static string ToIso(DateTimeOffset dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string? ToIsoOrNull(DateTimeOffset? dt) => dt.HasValue ? ToIso(dt.Value) : null;
}
