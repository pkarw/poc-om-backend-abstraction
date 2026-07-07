using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// /api/dashboards/roles/widgets — 1:1 port of upstream api/roles/widgets/route.ts. GET returns the
/// widgets assigned to a role (most-specific matching record); PUT upserts (or deletes on empty).
/// Auth (401) + dashboards.admin.assign-widgets (403) enforced by the filter.
/// </summary>
public sealed class RolesWidgetsRouteGroup : IDashboardRouteGroup
{
    private const string Feature = "dashboards.admin.assign-widgets";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboards/roles/widgets", GetAsync).RequireFeatures(Feature);
        routes.MapPut("/api/dashboards/roles/widgets", PutAsync).RequireFeatures(Feature);
    }

    private static DashboardRoleWidgets? PickBestRecord(IEnumerable<DashboardRoleWidgets> records, Guid? tenantId, Guid? organizationId)
    {
        DashboardRoleWidgets? best = null;
        var bestScore = -1;
        foreach (var r in records)
        {
            if (r.DeletedAt is not null) continue;
            if (r.TenantId.HasValue && tenantId.HasValue && r.TenantId != tenantId) continue;
            if (r.TenantId.HasValue && !tenantId.HasValue) continue;
            if (r.OrganizationId.HasValue && organizationId.HasValue && r.OrganizationId != organizationId) continue;
            if (r.OrganizationId.HasValue && !organizationId.HasValue) continue;
            var score = (r.TenantId.HasValue ? 1 : 0) + (r.OrganizationId.HasValue ? 2 : 0);
            if (score > bestScore) { best = r; bestScore = score; }
        }
        return best;
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var q = http.Request.Query;
        var roleIdRaw = q["roleId"].ToString();
        if (string.IsNullOrEmpty(roleIdRaw) || !Guid.TryParse(roleIdRaw, out var roleId))
            return Results.Json(new { error = "roleId is required" }, statusCode: 400);

        var acl = await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId);
        var scope = WidgetAssignmentScope.ResolveReadScope(
            auth.TenantId, auth.OrganizationId, acl.IsSuperAdmin,
            DashboardRouteHelpers.Guid(q["tenantId"].ToString()),
            DashboardRouteHelpers.Guid(q["organizationId"].ToString()));

        var role = await db.Set<Role>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.DeletedAt == null);
        if (role is null || (scope.TenantId.HasValue && role.TenantId != scope.TenantId))
            return Results.Json(new { error = "Role not found" }, statusCode: 404);

        var records = await db.Set<DashboardRoleWidgets>().AsNoTracking()
            .Where(r => r.RoleId == roleId && r.DeletedAt == null).ToListAsync();
        var best = PickBestRecord(records, scope.TenantId, scope.OrganizationId);

        var widgetIds = new JsonArray();
        if (best is not null) foreach (var id in JsonStrings.ParseArray(best.WidgetIdsJson)) widgetIds.Add(id);

        return DashboardRouteHelpers.JsonContent(new JsonObject
        {
            ["widgetIds"] = widgetIds,
            ["hasCustom"] = best is not null,
            ["scope"] = new JsonObject
            {
                ["tenantId"] = scope.TenantId?.ToString(),
                ["organizationId"] = scope.OrganizationId?.ToString(),
            },
        });
    }

    private static async Task<IResult> PutAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await DashboardRouteHelpers.ReadJsonAsync(http);
        if (!body.Parsed) return Results.Json(new { error = "Invalid JSON body" }, statusCode: 400);

        var obj = body.Node as JsonObject;
        var roleIdRaw = DashboardRouteHelpers.Str(obj?["roleId"]);
        var widgetIdsNode = obj?["widgetIds"] as JsonArray;
        if (obj is null || roleIdRaw is null || !Guid.TryParse(roleIdRaw, out var roleId) || widgetIdsNode is null)
            return DashboardRouteHelpers.JsonContent(new JsonObject
            {
                ["error"] = "Invalid payload",
                ["issues"] = new JsonArray(),
            }, 400);

        var requested = widgetIdsNode.Select(DashboardRouteHelpers.Str)
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        var validIds = WidgetCatalog.AllIds().ToHashSet();
        var widgetIds = requested.Where(validIds.Contains).ToList();

        var tenantId = auth.TenantId;
        var organizationId = auth.OrganizationId;

        var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == roleId && r.DeletedAt == null);
        if (role is null || (tenantId.HasValue && role.TenantId != tenantId))
            return Results.Json(new { error = "Role not found" }, statusCode: 404);

        var record = await db.Set<DashboardRoleWidgets>()
            .FirstOrDefaultAsync(r => r.RoleId == roleId && r.TenantId == tenantId
                && r.OrganizationId == organizationId && r.DeletedAt == null);

        if (widgetIds.Count == 0)
        {
            if (record is not null) { db.Set<DashboardRoleWidgets>().Remove(record); await db.SaveChangesAsync(); }
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["ok"] = true, ["widgetIds"] = new JsonArray() });
        }

        if (record is null)
        {
            record = new DashboardRoleWidgets
            {
                Id = Guid.NewGuid(),
                RoleId = roleId,
                TenantId = tenantId,
                OrganizationId = organizationId,
                WidgetIdsJson = JsonStrings.SerializeArray(widgetIds),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Set<DashboardRoleWidgets>().Add(record);
        }
        else
        {
            record.WidgetIdsJson = JsonStrings.SerializeArray(widgetIds);
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        var outIds = new JsonArray();
        foreach (var id in widgetIds) outIds.Add(id);
        return DashboardRouteHelpers.JsonContent(new JsonObject { ["ok"] = true, ["widgetIds"] = outIds });
    }
}
