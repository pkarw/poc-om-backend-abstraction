using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Product media — the port of upstream <c>api/product-media/route.ts</c>. <c>GET
/// /api/catalog/product-media?productId=</c> validates the productId (400) and product scope (404), then
/// lists the product's media attachments. Requires <c>catalog.products.view</c>.
///
/// PARITY-TODO: the attachments module is not ported, so the attachment lookup + thumbnail URL building
/// (upstream reads the <c>attachments</c> table keyed by <c>catalog:catalog_product</c>) is deferred —
/// this returns an empty <c>items</c> list for an in-scope product. The validation contract is preserved.
/// </summary>
public sealed class ProductMediaRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };

    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/catalog/product-media", (Func<HttpContext, Task<IResult>>)ListAsync);

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var q = http.Request.Query;
        if (!Guid.TryParse(q["productId"].ToString(), out var productId))
            return CatalogHttp.Json(new { error = "productId is required" }, 400);

        var (ctx, denied) = await CatalogHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var exists = await db.Set<CatalogProduct>().AsNoTracking().AnyAsync(p =>
            p.Id == productId &&
            (ctx!.OrganizationId == null || p.OrganizationId == ctx.OrganizationId) &&
            (ctx.TenantId == null || p.TenantId == ctx.TenantId));
        if (!exists) return CatalogHttp.Json(new { error = "Product not found" }, 404);

        // Attachments module not ported — no media rows to project (see class remarks).
        return CatalogHttp.Json(new { items = Array.Empty<object>() }, 200);
    }
}
