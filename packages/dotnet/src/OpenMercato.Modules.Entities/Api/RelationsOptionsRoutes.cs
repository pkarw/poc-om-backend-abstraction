using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>GET /api/entities/relations/options</c> (upstream api/relations/options.ts) — resolves the option
/// rows for a relation-kind custom-field picker: <c>{ value, label, routeContext? }</c> for records of a
/// related entity, filtered by <c>?ids=</c> or a <c>?q=</c> substring. Reads whole docs via the Core
/// <see cref="IEntityLookupQuery"/> seam (query_index-backed). Requires <c>entities.definitions.view</c>.
/// </summary>
public static class RelationsOptionsRoutes
{
    // Only these keys may surface in routeContext (upstream ROUTE_CONTEXT_FIELD_ALLOWLIST).
    private static readonly HashSet<string> RouteContextAllowlist = new(StringComparer.Ordinal) { "kind", "entity_id", "product_id" };
    private static readonly string[] LabelCandidates = { "name", "title", "code", "email" };

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/relations/options", (Func<HttpContext, Task<IResult>>)GetAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.view");
        if (denied is not null) return denied;
        if (ctx!.TenantId is null || (ctx.OrganizationId is null && !ctx.IsSuperAdmin))
            return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var qp = http.Request.Query;
        var entityId = qp["entityId"].ToString();
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { items = Array.Empty<object>() }, 200);

        var q = qp["q"].ToString();
        var ids = CsvDistinct(qp["ids"].ToString());
        var routeContextFields = CsvDistinct(qp["routeContextFields"].ToString())
            .Where(RouteContextAllowlist.Contains).ToList();

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var labelField = await ResolveLabelFieldAsync(http, db, ctx, entityId, qp["labelField"].ToString());

        var fields = new List<string> { "id", labelField };
        foreach (var f in routeContextFields) if (!fields.Contains(f)) fields.Add(f);

        var pageSize = Math.Min(ids.Count == 0 ? 50 : ids.Count, 200);
        var lookup = http.RequestServices.GetRequiredService<IEntityLookupQuery>();
        var rows = await lookup.LookupAsync(new EntityLookupRequest(
            entityId, ctx.TenantId, ctx.OrganizationIds, ids,
            string.IsNullOrWhiteSpace(q) ? null : q, labelField, fields, pageSize));

        var items = new List<object>(rows.Count);
        foreach (var doc in rows)
        {
            var id = doc.TryGetValue("id", out var idv) ? idv?.ToString() : null;
            if (id is null) continue;
            var label = doc.TryGetValue(labelField, out var lv) && lv is not null ? lv.ToString() : id;

            var routeContext = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var f in routeContextFields)
                if (doc.TryGetValue(f, out var v) && v is not null) routeContext[f] = v;

            if (routeContext.Count > 0)
                items.Add(new { value = id, label, routeContext });
            else
                items.Add(new { value = id, label });
        }
        return EntitiesHttp.Result(new { items }, 200);
    }

    /// <summary>label field: explicit param → CustomEntity.LabelField (scoped) → first present candidate → 'id'.</summary>
    private static async Task<string> ResolveLabelFieldAsync(HttpContext http, AppDbContext db, OpenMercato.Core.Commands.CommandContext ctx, string entityId, string labelParam)
    {
        if (!string.IsNullOrWhiteSpace(labelParam)) return labelParam.Trim();

        var custom = await db.Set<CustomEntity>().AsNoTracking()
            .Where(e => e.EntityId == entityId && e.IsActive
                        && (e.OrganizationId == null || e.OrganizationId == ctx.OrganizationId)
                        && (e.TenantId == null || e.TenantId == ctx.TenantId))
            .OrderByDescending(e => (e.OrganizationId != null ? 2 : 0) + (e.TenantId != null ? 1 : 0))
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(custom?.LabelField)) return custom!.LabelField!;

        var lookup = http.RequestServices.GetRequiredService<IEntityLookupQuery>();
        foreach (var cand in LabelCandidates)
            if (await lookup.FieldExistsAsync(entityId, cand, ctx.TenantId, ctx.OrganizationIds)) return cand;
        return "id";
    }

    private static List<string> CsvDistinct(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(part)) list.Add(part);
        return list;
    }
}
