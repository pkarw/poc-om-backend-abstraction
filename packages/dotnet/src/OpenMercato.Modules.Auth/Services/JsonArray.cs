using System.Text.Json;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Helpers for the jsonb <c>string[]</c> columns (features_json / organizations_json). Upstream
/// stores these as native jsonb arrays; the .NET port stores the raw JSON string in the column
/// and (de)serializes on the boundary.
/// </summary>
public static class JsonArray
{
    /// <summary>Parse a jsonb string[] column into a list, or null when the column is null/blank/invalid.</summary>
    public static string[]? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialize a list to a jsonb string, or null when the list is null.</summary>
    public static string? Serialize(string[]? values) =>
        values is null ? null : JsonSerializer.Serialize(values);
}
