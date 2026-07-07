using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>Shared request/response helpers for the sidebar route groups.</summary>
public static class SidebarHttp
{
    /// <summary>An empty JSON object, used when a malformed/absent variant POST body is tolerated.</summary>
    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>JSON response with the sidebar serializer options (no HTML escaping).</summary>
    public static IResult Json(object? body, int status = 200) =>
        Results.Json(body, SidebarJson.Options, statusCode: status);

    /// <summary>Read + parse the request body as a JSON element; null on empty or malformed input.</summary>
    public static async Task<JsonElement?> ReadJsonAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, object?> RoleEntry(SidebarRoleEntry e) => new()
    {
        ["id"] = e.Id,
        ["name"] = e.Name,
        ["hasPreference"] = e.HasPreference,
    };

    public static Dictionary<string, object?> Variant(SidebarVariantRecord r) => new()
    {
        ["id"] = r.Id,
        ["name"] = r.Name,
        ["isActive"] = r.IsActive,
        ["settings"] = r.Settings.ToDict(),
        ["createdAt"] = SidebarJson.ToIso(r.CreatedAt),
        ["updatedAt"] = SidebarJson.ToIsoOrNull(r.UpdatedAt),
    };
}
