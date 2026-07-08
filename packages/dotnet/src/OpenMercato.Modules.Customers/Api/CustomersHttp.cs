using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Shared helpers for the hand-written customers routes (detail views, links, roles, labels, …) — the
/// analogue of upstream's <c>getAuthFromRequest</c> + <c>withScopedPayload</c> + response helpers. The
/// factory-generated CRUD routes use <see cref="CrudRoute"/> directly; these helpers cover everything
/// hand-written. Auth is resolved through <see cref="ICrudRequestContext"/> (the Auth-module bridge),
/// exactly as <c>CurrenciesRoutes</c> does for its non-factory endpoints.
/// </summary>
internal static class CustomersHttp
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IResult Json(object body, int status) => Results.Json(body, Web, statusCode: status);

    /// <summary>Resolve the request context (401 when unauthenticated) and check the given features (403).</summary>
    public static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(
        HttpContext http, string[]? features, string unauthorizedMessage = "Unauthorized")
    {
        var rc = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await rc.ResolveAsync(http);
        if (ctx is null) return (null, Json(new { error = unauthorizedMessage }, 401));
        if (features is { Length: > 0 } && !await rc.HasAllFeaturesAsync(ctx, features))
            return (null, Json(new { error = "Forbidden", requiredFeatures = features }, 403));
        return (ctx, null);
    }

    /// <summary>Re-check a feature on the current context (per-org RBAC recheck used by roles/labels).</summary>
    public static async Task<bool> HasFeatureAsync(HttpContext http, CommandContext ctx, string feature)
    {
        var rc = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        return await rc.HasAllFeaturesAsync(ctx, new[] { feature });
    }

    public static async Task<JsonElement> ReadBodyAsync(HttpContext http)
    {
        try
        {
            if (http.Request.ContentLength is 0) return Empty();
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return Empty(); }
    }

    private static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    // ---- JSON read helpers (mirror the currencies validators surface) -------------------------

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
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    public static int? Int(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static decimal? Decimal(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static float? Float(JsonElement body, string name)
    {
        var d = Decimal(body, name);
        return d is null ? null : (float)d.Value;
    }

    public static Guid? GuidOf(JsonElement body, string name)
    {
        var s = Str(body, name);
        return s is not null && Guid.TryParse(s, out var g) ? g : null;
    }

    public static DateTimeOffset? Date(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
            return d;
        return null;
    }

    /// <summary>ISO-8601 (Zulu) string for a timestamp, or null.</summary>
    public static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("o");
    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("o");
}
