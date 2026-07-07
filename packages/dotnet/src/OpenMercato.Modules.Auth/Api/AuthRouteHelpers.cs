using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Events;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Small shared helpers for the hand-written auth route groups: safe JSON body reading, query
/// coercion mirroring the upstream Zod schemas, LIKE escaping, and best-effort event emission.
/// </summary>
internal static class AuthRouteHelpers
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Read the request body as a JSON object; returns an empty object on malformed/non-object JSON (upstream <c>req.json().catch(() =&gt; ({}))</c>).</summary>
    public static async Task<JsonElement> ReadJsonObjectAsync(HttpContext http)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return EmptyObject;
            return doc.RootElement.Clone();
        }
        catch
        {
            return EmptyObject;
        }
    }

    public static bool TryGetString(this JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }
        return false;
    }

    public static bool HasProperty(this JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out _);

    public static bool IsNullProperty(this JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.Null;

    public static bool? TryGetBool(this JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    /// <summary>Optional string[] from a JSON array property; null when absent/not an array (mirrors <c>z.array(z.string()).optional()</c>).</summary>
    public static string[]? TryGetStringArray(this JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();
        }
        return null;
    }

    /// <summary>Escape a LIKE/ILIKE pattern (upstream escapeLikePattern): backslash-escapes % _ and \\.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Fire-and-forget event emission (upstream emitAuthEvent / persistent CRUD events). Never throws.</summary>
    public static async Task EmitAsync(HttpContext http, string eventName, object payload)
    {
        try
        {
            var bus = http.RequestServices.GetService<IEventBus>();
            if (bus is not null) await bus.PublishAsync(eventName, payload);
        }
        catch
        {
            // Best-effort — event delivery must never break a mutation (spec 04).
        }
    }
}
