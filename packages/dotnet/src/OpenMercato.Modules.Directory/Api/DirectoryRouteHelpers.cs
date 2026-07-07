using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using OpenMercato.Core.Events;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>Shared helpers for the hand-written directory route groups: JSON body reading, query
/// coercion (mirrors the Zod schemas), logoUrl/slug validation, and best-effort event emission.</summary>
internal static class DirectoryRouteHelpers
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static async Task<JsonElement> ReadJsonAsync(HttpContext http)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return EmptyObject; }
    }

    public static bool TryGetString(this JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String)
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

    public static bool CoerceIntWithDefault(StringValues raw, int def, int min, int max, out int value)
    {
        value = def;
        var s = raw.Count > 0 ? raw.ToString() : null;
        if (string.IsNullOrEmpty(s)) return true;
        if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return false;
        if (n < min || n > max) return false;
        value = (int)n;
        return value >= min;
    }

    /// <summary>parseBooleanToken: true/1/yes/on → true; false/0/no/off → false; else null.</summary>
    public static bool? ParseBooleanToken(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null,
        };
    }

    private static readonly Regex SlugRe = new("^[a-z0-9\\-_]+$", RegexOptions.Compiled);
    private static readonly Regex AttachmentRe =
        new("^/api/attachments/(?:image|file)/[A-Za-z0-9%_.~/?=&-]+$", RegexOptions.Compiled);

    /// <summary>Validate slugField: trim, lowercase, ^[a-z0-9\-_]+$, max 150. Returns normalized or fails.</summary>
    public static bool TryNormalizeSlug(string raw, out string normalized)
    {
        normalized = raw.Trim().ToLowerInvariant();
        return normalized.Length <= 150 && SlugRe.IsMatch(normalized);
    }

    /// <summary>Validate logoUrlField union: http(s) URL (max 2048) OR internal attachment path (max 2048).</summary>
    public static bool IsValidLogoUrl(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0 || v.Length > 2048) return false;
        if (v.StartsWith("https://") || v.StartsWith("http://"))
            return Uri.TryCreate(v, UriKind.Absolute, out _);
        return AttachmentRe.IsMatch(v);
    }

    public static async Task EmitAsync(HttpContext http, string eventName, object payload)
    {
        try
        {
            var bus = http.RequestServices.GetService<IEventBus>();
            if (bus is not null) await bus.PublishAsync(eventName, payload);
        }
        catch { /* best-effort (spec 04) */ }
    }
}
