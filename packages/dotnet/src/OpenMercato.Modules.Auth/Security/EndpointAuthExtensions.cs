using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Endpoint-filter helpers mirroring the upstream dispatcher guards (spec 05 R20/R22).
/// <see cref="RequireAuth"/> resolves the staff principal and returns 401
/// <c>{"error":"Unauthorized"}</c> when absent/invalid. <see cref="RequireFeatures"/> additionally
/// resolves <see cref="IRbacService"/> and returns 403 <c>{"error":"Forbidden","requiredFeatures":[...]}</c>
/// when the user lacks any required feature. The resolved <see cref="AuthContext"/> is stashed in
/// HttpContext.Items (see <see cref="HttpContextAuth.Current"/>).
/// </summary>
public static class EndpointAuthExtensions
{
    /// <summary>Require a valid authenticated staff principal.</summary>
    public static TBuilder RequireAuth<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var auth = await ResolveAsync(ctx.HttpContext);
            if (auth is null) return Unauthorized();
            return await next(ctx);
        });

    /// <summary>Require auth plus every listed feature.</summary>
    public static TBuilder RequireFeatures<TBuilder>(this TBuilder builder, params string[] features)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var auth = await ResolveAsync(http);
            if (auth is null) return Unauthorized();

            if (features.Length > 0)
            {
                var rbac = http.RequestServices.GetRequiredService<IRbacService>();
                var ok = await rbac.UserHasAllFeatures(auth.UserId, features, auth.TenantId, auth.OrganizationId);
                if (!ok)
                    return Results.Json(new { error = "Forbidden", requiredFeatures = features }, statusCode: 403);
            }
            return await next(ctx);
        });

    private static async Task<AuthContext?> ResolveAsync(HttpContext http)
    {
        var existing = HttpContextAuth.Current(http);
        if (existing is not null) return existing;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var jwt = http.RequestServices.GetRequiredService<JwtService>();
        return await HttpContextAuth.ResolveAsync(http, db, jwt);
    }

    private static IResult Unauthorized() =>
        Results.Json(new { error = "Unauthorized" }, statusCode: 401);
}
