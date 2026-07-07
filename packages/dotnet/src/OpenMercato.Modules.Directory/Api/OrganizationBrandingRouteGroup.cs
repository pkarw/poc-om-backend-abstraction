using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// /api/directory/organization-branding — 1:1 port of upstream api/organization-branding/route.ts.
/// GET returns the current org's branding; PUT updates logoUrl (422 on invalid/missing logoUrl).
/// Org-scope is resolved from auth.orgId/auth.tenantId (full org-scope resolver + cache-tag
/// invalidation are PARITY-TODO). i18n message strings use the inline English fallbacks.
/// </summary>
public sealed class OrganizationBrandingRouteGroup : IDirectoryRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/directory/organization-branding", GetAsync).RequireFeatures("directory.organizations.view");
        routes.MapPut("/api/directory/organization-branding", PutAsync).RequireFeatures("directory.organizations.manage");
    }

    private static (Guid orgId, Guid tenantId)? ResolveScope(AuthContext auth) =>
        auth.OrganizationId is { } o && auth.TenantId is { } t ? (o, t) : null;

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var scope = ResolveScope(auth);
        if (scope is null)
            return Results.Json(new { error = "Select a single organization before changing sidebar branding." }, statusCode: 400);

        var (orgId, tenantId) = scope.Value;
        var org = await db.Set<Organization>().AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orgId && o.TenantId == tenantId && o.DeletedAt == null);
        if (org is null) return Results.Json(new { error = "Organization not found" }, statusCode: 404);

        return Results.Json(new
        {
            organizationId = org.Id.ToString(),
            organizationName = org.Name,
            tenantId = org.TenantId.ToString(),
            logoUrl = org.LogoUrl,
        });
    }

    private static async Task<IResult> PutAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var scope = ResolveScope(auth);
        if (scope is null)
            return Results.Json(new { error = "Select a single organization before changing sidebar branding." }, statusCode: 400);

        var (orgId, tenantId) = scope.Value;

        JsonElement body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            body = doc.RootElement.Clone();
        }
        catch { return Results.Json(new { error = "Enter a valid image URL." }, statusCode: 422); }

        // Body must be a JSON object that OWNS a logoUrl key.
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("logoUrl", out var logoEl))
            return Results.Json(new { error = "Enter a valid image URL." }, statusCode: 422);

        string? logoUrl;
        if (logoEl.ValueKind == JsonValueKind.Null) logoUrl = null;
        else if (logoEl.ValueKind == JsonValueKind.String && DirectoryRouteHelpers.IsValidLogoUrl(logoEl.GetString()!))
            logoUrl = logoEl.GetString()!.Trim();
        else
            return Results.Json(new { error = "Enter a valid image URL.", issues = Array.Empty<object>() }, statusCode: 422);

        var org = await db.Set<Organization>().FirstOrDefaultAsync(o => o.Id == orgId && o.TenantId == tenantId && o.DeletedAt == null);
        if (org is null) return Results.Json(new { error = "Organization not found" }, statusCode: 404);

        org.LogoUrl = logoUrl;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Emitted through the shared directory.organizations.update command upstream.
        await DirectoryRouteHelpers.EmitAsync(http, "directory.organization.updated",
            new { id = org.Id.ToString(), tenantId = org.TenantId.ToString(), organizationId = org.Id.ToString() });
        // PARITY-TODO: runWithCacheTenant + cache.deleteByTags(nav:sidebar:*) invalidation.

        return Results.Json(new
        {
            organizationId = org.Id.ToString(),
            organizationName = org.Name,
            tenantId = org.TenantId.ToString(),
            logoUrl = org.LogoUrl,
        });
    }
}
