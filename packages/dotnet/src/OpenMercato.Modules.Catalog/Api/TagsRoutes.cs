using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Tags — the port of upstream <c>api/tags/route.ts</c>. Read-only: <c>GET /api/catalog/tags</c> lists the
/// free product-tag pool (label ILIKE search, paged, ordered by label). Requires
/// <c>catalog.products.view</c>. Tags are created implicitly by the products slice (via the product
/// <c>tags</c> nested write), so there is no tag mutation route.
/// </summary>
public sealed class TagsRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };

    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/catalog/tags", (Func<HttpContext, Task<IResult>>)ListAsync);

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await CatalogHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;

        if (ctx!.TenantId is not { } tenantId)
            return CatalogHttp.Json(new { items = Array.Empty<object>(), error = "Tenant context is required." }, 400);
        if (ctx.OrganizationId is not { } organizationId)
            return CatalogHttp.Json(new { items = Array.Empty<object>(), error = "Organization context is required." }, 400);

        var q = http.Request.Query;
        var page = ParseInt(q["page"], 1, 1, int.MaxValue);
        var pageSize = ParseInt(q["pageSize"], 50, 1, 200);
        var search = q["search"].ToString()?.Trim();

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var query = db.Set<CatalogProductTag>().AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.TenantId == tenantId);
        if (!string.IsNullOrEmpty(search))
        {
            var term = search.ToLowerInvariant();
            query = query.Where(t => t.Label.ToLower().Contains(term));
        }

        var total = await query.CountAsync();
        var records = await query.OrderBy(t => t.Label)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var items = records.Select(t => new
        {
            id = t.Id.ToString(),
            label = t.Label,
            slug = t.Slug,
            createdAt = CatalogHttp.Iso(t.CreatedAt),
            updatedAt = CatalogHttp.Iso(t.UpdatedAt),
        }).ToList();

        return CatalogHttp.Json(new { items, total }, 200);
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }
}
