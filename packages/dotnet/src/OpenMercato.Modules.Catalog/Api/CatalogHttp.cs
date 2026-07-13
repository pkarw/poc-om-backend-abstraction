using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Small JSON body-reading helpers shared by the catalog CRUD dispatch delegates and command handlers
/// (mirrors the customers module's <c>CustomersHttp</c>/<c>J</c> readers). The CRUD factory owns auth,
/// the response envelope and status codes, so these are only the typed field accessors over the raw
/// request body (<see cref="JsonElement"/>).
/// </summary>
public static class CatalogHttp
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IResult Json(object body, int status) => Results.Json(body, Web, statusCode: status);

    /// <summary>Resolve the request context (401 when unauthenticated) and check the given features (403).
    /// Used by the hand-written catalog GET endpoints; the CRUD factory does this itself for its routes.</summary>
    public static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, string[]? features)
    {
        var rc = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await rc.ResolveAsync(http);
        if (ctx is null) return (null, Json(new { error = "Unauthorized" }, 401));
        if (features is { Length: > 0 } && !await rc.HasAllFeaturesAsync(ctx, features))
            return (null, Json(new { error = "Forbidden", requiredFeatures = features }, 403));
        return (ctx, null);
    }

    public static bool Has(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out _);

    public static string? Str(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static bool? Bool(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static int? Int(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static decimal? Decimal(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static Guid? GuidOf(JsonElement body, string name)
    {
        var s = Str(body, name);
        return Guid.TryParse(s, out var g) ? g : null;
    }

    public static DateTimeOffset? Date(JsonElement body, string name)
    {
        var s = Str(body, name);
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>The raw jsonb text of an object/array property (null when absent/null), for storing into
    /// jsonb columns verbatim (metadata/dimensions/option_values/schema).</summary>
    public static string? RawJson(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        return v.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? v.GetRawText() : null;
    }

    /// <summary>Reads an array-of-strings property as a list of trimmed non-empty strings (e.g. tags,
    /// categoryIds).</summary>
    public static IReadOnlyList<string> StringArray(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in v.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var s = el.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Parse a stored jsonb string back into a value that serializes as an object/array (so list
    /// items echo <c>metadata</c> as JSON, not a quoted string). Null/blank → null.</summary>
    public static object? JsonValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return null; }
    }

    public static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("o");
    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("o");
}
