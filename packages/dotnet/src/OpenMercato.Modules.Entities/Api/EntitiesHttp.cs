using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>Shared HTTP helpers for the entities routes: auth resolution (via the Core CRUD auth
/// bridge <see cref="ICrudRequestContext"/>), feature checks, and JSON responses.</summary>
internal static class EntitiesHttp
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static IResult Result(object body, int status) => Results.Json(body, Json, statusCode: status);

    /// <summary>Resolve the authenticated <see cref="CommandContext"/> + enforce features. Returns a
    /// denial IResult (401/403) when unauthenticated or lacking a required feature.</summary>
    internal static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, params string[] features)
    {
        var bridge = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await bridge.ResolveAsync(http);
        if (ctx is null) return (null, Result(new { error = "Unauthorized" }, 401));
        if (features.Length > 0 && !await bridge.HasAllFeaturesAsync(ctx, features))
            return (null, Result(new { error = "Forbidden", requiredFeatures = features }, 403));
        return (ctx, null);
    }

    internal static async Task<JsonElement> ReadBodyAsync(HttpContext http)
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

    internal static string? StringProp(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
