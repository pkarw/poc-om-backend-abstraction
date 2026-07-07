using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Port of <c>api/sidebar/variants/[id]/route.ts</c>: GET/PUT/DELETE
/// <c>/api/auth/sidebar/variants/{id}</c>. Upstream extracts the id as the last URL path segment with
/// no UUID validation, so a non-parseable id resolves to a 404 "Variant not found". PUT with
/// <c>isActive:true</c> deactivates other variants in scope; DELETE soft-deletes.
/// </summary>
public sealed class SidebarVariantIdRoutes : IAuthRouteGroup
{
    private const string FeatureManage = "auth.sidebar.manage";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/sidebar/variants/{id}", GetAsync).RequireAuth();
        routes.MapPut("/api/auth/sidebar/variants/{id}", PutAsync).RequireFeatures(FeatureManage);
        routes.MapDelete("/api/auth/sidebar/variants/{id}", DeleteAsync).RequireFeatures(FeatureManage);
    }

    private static async Task<IResult> GetAsync(HttpContext http, string id)
    {
        var auth = HttpContextAuth.Current(http)!;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var svc = new SidebarPreferencesService(db);

        if (!Guid.TryParse(id, out var variantId))
            return NotFound();

        var locale = SidebarLocale.Resolve(http);
        var variant = await svc.LoadSidebarVariantAsync(auth.UserId, auth.TenantId, variantId);
        if (variant is null) return NotFound();

        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["variant"] = SidebarHttp.Variant(variant),
        });
    }

    private static async Task<IResult> PutAsync(HttpContext http, string id)
    {
        var auth = HttpContextAuth.Current(http)!;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var svc = new SidebarPreferencesService(db);

        var body = await SidebarHttp.ReadJsonAsync(http);
        if (body is null)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid JSON" }, 400);

        var parsed = SidebarValidation.ValidateVariant(body.Value);
        if (!parsed.Ok)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid payload", ["details"] = parsed.Details }, 400);

        if (!Guid.TryParse(id, out var variantId))
            return NotFound();

        var locale = SidebarLocale.Resolve(http);
        var input = parsed.Value!;
        var variant = await svc.UpdateSidebarVariantAsync(
            auth.UserId, auth.TenantId, variantId,
            input.Name,
            input.Settings?.ToSettings(),
            input.IsActive);

        if (variant is null) return NotFound();

        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["variant"] = SidebarHttp.Variant(variant),
        });
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string id)
    {
        var auth = HttpContextAuth.Current(http)!;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var svc = new SidebarPreferencesService(db);

        if (!Guid.TryParse(id, out var variantId))
            return NotFound();

        var ok = await svc.DeleteSidebarVariantAsync(auth.UserId, auth.TenantId, variantId);
        if (!ok) return NotFound();

        return SidebarHttp.Json(new Dictionary<string, object?> { ["ok"] = true });
    }

    private static IResult NotFound() =>
        SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Variant not found" }, 404);
}
