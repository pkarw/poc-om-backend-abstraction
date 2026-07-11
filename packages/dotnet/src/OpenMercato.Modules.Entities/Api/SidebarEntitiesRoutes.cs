using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>GET /api/entities/sidebar-entities</c> (upstream api/sidebar-entities.ts) — the custom entities the
/// caller pinned to the sidebar (<c>showInSidebar</c>). Tenant-scoped only (org filter intentionally off,
/// mirroring upstream). Auth required, no feature gate. The upstream nav cache is a transparent perf layer
/// and is omitted (no cache seam wired for the entities routes).
/// </summary>
public static class SidebarEntitiesRoutes
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/sidebar-entities", (Func<HttpContext, Task<IResult>>)GetAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http);
        if (denied is not null) return denied;
        if (ctx!.TenantId is null || (ctx.OrganizationId is null && !ctx.IsSuperAdmin))
            return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var rows = await db.Set<CustomEntity>().AsNoTracking()
            .Where(e => e.IsActive && e.ShowInSidebar && (e.TenantId == null || e.TenantId == ctx.TenantId))
            .OrderBy(e => e.Label)
            .Select(e => new { e.EntityId, e.Label })
            .ToListAsync();

        var items = rows.Select(e => new
        {
            entityId = e.EntityId,
            label = e.Label,
            href = "/backend/entities/user/" + Uri.EscapeDataString(e.EntityId) + "/records",
        }).ToList();

        return EntitiesHttp.Result(new { items }, 200);
    }
}
