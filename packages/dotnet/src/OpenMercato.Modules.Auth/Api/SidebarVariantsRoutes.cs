using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Port of <c>api/sidebar/variants/route.ts</c>: GET + POST <c>/api/auth/sidebar/variants</c>.
/// GET requires auth and sends <c>cache-control: no-store, no-cache, must-revalidate</c>; POST gates
/// <c>auth.sidebar.manage</c>, auto-names blank variants, maps the unique-name violation to 409
/// <c>{code:'duplicate_name'}</c>, and returns 200 (not 201) on success.
/// </summary>
public sealed class SidebarVariantsRoutes : IAuthRouteGroup
{
    private const string FeatureManage = "auth.sidebar.manage";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/sidebar/variants", GetAsync).RequireAuth();
        routes.MapPost("/api/auth/sidebar/variants", PostAsync).RequireFeatures(FeatureManage);
    }

    // Must take a DI parameter besides HttpContext: a lone-HttpContext handler is bound as a
    // RequestDelegate and its returned IResult is ignored (blank 200). See SidebarPreferencesRoutes.
    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var svc = new SidebarPreferencesService(db);
        var locale = SidebarLocale.Resolve(http);

        var variants = await svc.ListSidebarVariantsAsync(auth.UserId, auth.TenantId);

        http.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["variants"] = variants.Select(SidebarHttp.Variant).ToList(),
        });
    }

    private static async Task<IResult> PostAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var svc = new SidebarPreferencesService(db);

        // Malformed / empty body is tolerated: treated as {} → creates an auto-named default variant.
        var body = await SidebarHttp.ReadJsonAsync(http) ?? SidebarHttp.EmptyObject;

        var parsed = SidebarValidation.ValidateVariant(body);
        if (!parsed.Ok)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid payload", ["details"] = parsed.Details }, 400);

        try
        {
            var locale = SidebarLocale.Resolve(http);
            var input = parsed.Value!;
            var variant = await svc.CreateSidebarVariantAsync(
                auth.UserId, auth.TenantId, auth.OrganizationId, locale,
                input.Name,
                (input.Settings ?? new SidebarSettingsInput()).ToSettings(),
                input.IsActive == true);

            return SidebarHttp.Json(new Dictionary<string, object?>
            {
                ["locale"] = locale,
                ["variant"] = SidebarHttp.Variant(variant),
            });
        }
        catch (Exception ex) when (SidebarPreferencesService.IsUniqueViolation(ex))
        {
            return SidebarHttp.Json(new Dictionary<string, object?>
            {
                ["error"] = "A variant with this name already exists. Choose a different name.",
                ["code"] = "duplicate_name",
            }, 409);
        }
        catch (Exception ex)
        {
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = ex.Message }, 500);
        }
    }
}
