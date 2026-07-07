using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMercato.Modules.Dashboards.Lib;

/// <summary>Helpers for the raw jsonb string-array columns (widget_ids_json) and item serialization.</summary>
internal static class JsonStrings
{
    /// <summary>Parse a jsonb string[] column into a list of strings (empty on null/invalid).</summary>
    public static List<string> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<string>();
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Serialize a string sequence to a compact jsonb array literal.</summary>
    public static string SerializeArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values) arr.Add(v);
        return arr.ToJsonString();
    }
}
