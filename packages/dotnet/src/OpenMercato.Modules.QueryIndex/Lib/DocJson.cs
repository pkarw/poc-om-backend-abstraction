using System.Globalization;
using System.Text.Json;

namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>
/// JSON helpers for the <c>doc jsonb</c> column: parse a stored doc into a CLR dictionary, serialize a
/// built doc back to canonical JSON, and render a value as the text Postgres' <c>doc ->> key</c> would
/// yield (used by the query engine's in-memory filter/sort evaluation).
/// </summary>
public static class DocJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Parse a stored doc JSON object into a mutable CLR dictionary (values as CLR primitives/lists).</summary>
    public static Dictionary<string, object?> ParseObject(string? json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = ToClr(prop.Value);
        }
        catch { /* malformed doc → empty */ }
        return result;
    }

    /// <summary>Serialize a built doc to JSON for the <c>doc</c> jsonb column.</summary>
    public static string Serialize(IReadOnlyDictionary<string, object?> doc)
        => JsonSerializer.Serialize(doc, Options);

    /// <summary>Convert a <see cref="JsonElement"/> to a CLR value (string/long/double/bool/null/list/dict).</summary>
    public static object? ToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => el.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    /// <summary>
    /// Render a doc value the way Postgres' <c>doc ->> key</c> would (text). Scalars → their text form;
    /// arrays/objects → their JSON text; null → null. Used for the engine's text comparisons + sorting.
    /// </summary>
    public static string? ToText(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => ((double)f).ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value, Options),
    };
}
