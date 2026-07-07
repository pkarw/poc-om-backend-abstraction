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
/// /api/dashboards/users/widgets — 1:1 port of upstream api/users/widgets/route.ts. GET returns the
/// target user's override state plus their effective allowed widget ids; PUT sets/clears the
/// override (mode=inherit deletes the record). Auth (401) + dashboards.admin.assign-widgets (403)
/// enforced by the filter.
/// </summary>
public sealed class UsersWidgetsRouteGroup : IDashboardRouteGroup
{
    private const string Feature = "dashboards.admin.assign-widgets";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboards/users/widgets", GetAsync).RequireFeatures(Feature);
        routes.MapPut("/api/dashboards/users/widgets", PutAsync).RequireFeatures(Feature);
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var q = http.Request.Query;
        var userIdRaw = q["userId"].ToString();
        if (string.IsNullOrEmpty(userIdRaw) || !Guid.TryParse(userIdRaw, out var userId))
            return Results.Json(new { error = "userId is required" }, statusCode: 400);

        var acl = await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId);
        var scope = WidgetAssignmentScope.ResolveReadScope(
            auth.TenantId, auth.OrganizationId, acl.IsSuperAdmin,
            DashboardRouteHelpers.Guid(q["tenantId"].ToString()),
            DashboardRouteHelpers.Guid(q["organizationId"].ToString()));

        var targetUser = await db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        if (targetUser is null || (scope.TenantId.HasValue && (targetUser.TenantId ?? null) != scope.TenantId))
            return Results.Json(new { error = "User not found" }, statusCode: 404);

        var widgets = WidgetCatalog.LoadAll();
        var targetAcl = await rbac.LoadAcl(userId, scope.TenantId, scope.OrganizationId);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db,
            new WidgetAccessContext(userId, scope.TenantId, scope.OrganizationId, targetAcl.Features, targetAcl.IsSuperAdmin), widgets);

        var record = await db.Set<DashboardUserWidgets>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == userId && u.TenantId == scope.TenantId
                && u.OrganizationId == scope.OrganizationId && u.DeletedAt == null);

        var widgetIds = new JsonArray();
        if (record is not null && record.Mode == "override")
            foreach (var id in JsonStrings.ParseArray(record.WidgetIdsJson)) widgetIds.Add(id);
        var effective = new JsonArray();
        foreach (var id in allowed) effective.Add(id);

        return DashboardRouteHelpers.JsonContent(new JsonObject
        {
            ["mode"] = record?.Mode ?? "inherit",
            ["widgetIds"] = widgetIds,
            ["hasCustom"] = record is not null && record.Mode == "override",
            ["effectiveWidgetIds"] = effective,
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
        var userIdRaw = DashboardRouteHelpers.Str(obj?["userId"]);
        var widgetIdsNode = obj?["widgetIds"] as JsonArray;
        var mode = DashboardRouteHelpers.Str(obj?["mode"]) ?? "inherit"; // schema default 'inherit'
        if (obj is null || userIdRaw is null || !Guid.TryParse(userIdRaw, out var userId)
            || widgetIdsNode is null || mode is not ("inherit" or "override"))
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid payload", ["issues"] = new JsonArray() }, 400);

        var requested = widgetIdsNode.Select(DashboardRouteHelpers.Str)
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        var validIds = WidgetCatalog.AllIds().ToHashSet();
        var widgetIds = requested.Where(validIds.Contains).ToList();

        var tenantId = auth.TenantId;
        var organizationId = auth.OrganizationId;

        var targetUser = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        if (targetUser is null || (tenantId.HasValue && (targetUser.TenantId ?? null) != tenantId))
            return Results.Json(new { error = "User not found" }, statusCode: 404);

        var record = await db.Set<DashboardUserWidgets>()
            .FirstOrDefaultAsync(u => u.UserId == userId && u.TenantId == tenantId
                && u.OrganizationId == organizationId && u.DeletedAt == null);

        if (mode == "inherit")
        {
            if (record is not null) { db.Set<DashboardUserWidgets>().Remove(record); await db.SaveChangesAsync(); }
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["ok"] = true, ["mode"] = "inherit", ["widgetIds"] = new JsonArray() });
        }

        if (record is null)
        {
            record = new DashboardUserWidgets
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantId = tenantId,
                OrganizationId = organizationId,
                Mode = "override",
                WidgetIdsJson = JsonStrings.SerializeArray(widgetIds),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Set<DashboardUserWidgets>().Add(record);
        }
        else
        {
            record.Mode = "override";
            record.WidgetIdsJson = JsonStrings.SerializeArray(widgetIds);
            record.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        var outIds = new JsonArray();
        foreach (var id in widgetIds) outIds.Add(id);
        return DashboardRouteHelpers.JsonContent(new JsonObject { ["ok"] = true, ["mode"] = "override", ["widgetIds"] = outIds });
    }
}
