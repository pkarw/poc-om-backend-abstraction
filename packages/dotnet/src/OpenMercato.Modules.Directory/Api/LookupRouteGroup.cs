using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// Public lookup endpoints (upstream api/get/organizations/lookup.ts + api/get/tenants/lookup.ts).
/// No auth (requireAuth:false), no features, no audit/cache/decryption.
/// </summary>
public sealed class LookupRouteGroup : IDirectoryRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        // GET /api/directory/organizations/lookup?slug=
        routes.MapGet("/api/directory/organizations/lookup", async (HttpContext http, AppDbContext db) =>
        {
            var slug = http.Request.Query["slug"].ToString();
            if (string.IsNullOrEmpty(slug) || slug.Length < 1 || slug.Length > 150)
                return Results.Json(new { ok = false, error = "Invalid slug." }, statusCode: 400);

            var org = await db.Set<Organization>().AsNoTracking()
                .FirstOrDefaultAsync(o => o.Slug == slug && o.DeletedAt == null);
            if (org is null)
                return Results.Json(new { ok = false, error = "Organization not found." }, statusCode: 404);

            return Results.Json(new
            {
                ok = true,
                organization = new { id = org.Id.ToString(), name = org.Name, slug = org.Slug },
            });
        });

        // GET /api/directory/tenants/lookup?tenantId= (fallback ?tenant=)
        routes.MapGet("/api/directory/tenants/lookup", async (HttpContext http, AppDbContext db) =>
        {
            var raw = http.Request.Query["tenantId"].ToString();
            if (string.IsNullOrEmpty(raw)) raw = http.Request.Query["tenant"].ToString();
            if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var tenantId))
                return Results.Json(new { ok = false, error = "Invalid tenant id." }, statusCode: 400);

            var tenant = await db.Set<Tenant>().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null);
            if (tenant is null)
                return Results.Json(new { ok = false, error = "Tenant not found." }, statusCode: 404);

            return Results.Json(new { ok = true, tenant = new { id = tenant.Id.ToString(), name = tenant.Name } });
        });
    }
}
