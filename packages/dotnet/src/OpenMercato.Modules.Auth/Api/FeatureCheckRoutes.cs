using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// POST /api/auth/feature-check (auth required). Mirrors upstream api/feature-check.ts. NOTE: the
/// 401 body is <c>{ok:false,error:"Unauthorized"}</c> (not the foundation filter's shape), so auth is
/// resolved in-handler. Evaluates rbac.UserHasAllFeatures; on a batch miss it re-checks each feature
/// individually to build the <c>granted</c> list.
/// </summary>
public sealed class FeatureCheckRoutes : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/feature-check", Handle);
    }

    private static async Task<IResult> Handle(HttpContext http, AppDbContext db, JwtService jwt, CancellationToken ct)
    {
        var auth = await HttpContextAuth.ResolveAsync(http, db, jwt);
        if (auth is null)
            return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

        string[]? features = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            features = ParseFeatures(doc.RootElement);
        }
        catch
        {
            // malformed JSON -> features stays null -> 400 below
        }

        // featureCheckRequestSchema: features array (each max 128 chars) max 50.
        if (features is null)
            return Results.Json(new { ok = false, error = "Invalid request" }, statusCode: 400);

        var userId = auth.UserId.ToString();
        if (features.Length == 0)
            return Results.Json(new { ok = true, granted = Array.Empty<string>(), userId });

        var rbac = http.RequestServices.GetRequiredService<IRbacService>();
        var all = await rbac.UserHasAllFeatures(auth.UserId, features, auth.TenantId, auth.OrganizationId);
        if (all)
            return Results.Json(new { ok = true, granted = features, userId });

        var granted = new List<string>();
        foreach (var f in features)
        {
            if (await rbac.UserHasAllFeatures(auth.UserId, new[] { f }, auth.TenantId, auth.OrganizationId))
                granted.Add(f);
        }
        return Results.Json(new { ok = false, granted, userId });
    }

    private static string[]? ParseFeatures(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("features", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) return null;
            var value = e.GetString()!;
            if (value.Length > 128) return null;
            list.Add(value);
        }
        if (list.Count > 50) return null;
        return list.ToArray();
    }
}
