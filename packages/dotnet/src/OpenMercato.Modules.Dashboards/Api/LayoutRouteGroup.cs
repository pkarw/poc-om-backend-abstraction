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
/// /api/dashboards/layout — 1:1 port of upstream api/layout/route.ts. GET loads-or-creates the
/// caller's layout, filters it to currently-allowed widgets, densely re-indexes, persists any
/// change, and returns the envelope DashboardScreen consumes. PUT persists the full layout. Auth
/// (401) + feature gating (403) are enforced by the RequireFeatures filter (dashboards.view /
/// dashboards.configure). All 10 built-in widgets ship defaultEnabled=false, so a first-time user's
/// default layout is empty (items:[]).
/// </summary>
public sealed class LayoutRouteGroup : IDashboardRouteGroup
{
    private const string DefaultSize = "md";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboards/layout", GetAsync).RequireFeatures("dashboards.view");
        routes.MapPut("/api/dashboards/layout", PutAsync).RequireFeatures("dashboards.configure");
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac, EncryptionService encryption)
    {
        var auth = HttpContextAuth.Current(http)!;
        var userId = auth.UserId;
        var tenantId = auth.TenantId;
        var organizationId = auth.OrganizationId;

        var acl = await rbac.LoadAcl(userId, tenantId, organizationId);
        var widgets = WidgetCatalog.LoadAll();
        var allowedIds = await WidgetAccess.ResolveAllowedWidgetIds(db,
            new WidgetAccessContext(userId, tenantId, organizationId, acl.Features, acl.IsSuperAdmin), widgets);
        var allowedSet = allowedIds.ToHashSet();
        var allowedWidgets = widgets.Where(w => allowedSet.Contains(w.Id)).ToList();

        var layout = await db.Set<DashboardLayout>()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TenantId == tenantId
                && l.OrganizationId == organizationId && l.DeletedAt == null);

        List<JsonObject> items;
        var hasChanged = false;

        if (layout is null)
        {
            items = allowedWidgets.Where(w => w.DefaultEnabled).Select((w, idx) =>
            {
                var obj = new JsonObject
                {
                    ["id"] = System.Guid.NewGuid().ToString(),
                    ["widgetId"] = w.Id,
                    ["order"] = idx,
                    ["priority"] = idx,
                    ["size"] = w.DefaultSize ?? DefaultSize,
                };
                if (w.DefaultSettings is not null) obj["settings"] = w.DefaultSettings.DeepClone();
                return obj;
            }).ToList();

            var now = DateTimeOffset.UtcNow;
            layout = new DashboardLayout
            {
                Id = System.Guid.NewGuid(),
                UserId = userId,
                TenantId = tenantId,
                OrganizationId = organizationId,
                LayoutJson = LayoutJson.Serialize(items),
                CreatedAt = now,
            };
            db.Set<DashboardLayout>().Add(layout);
            hasChanged = true;
        }
        else
        {
            var stored = LayoutJson.Parse(layout.LayoutJson);
            items = LayoutJson.Normalize(stored);
            var beforeFilter = items.Count;
            items = items.Where(i => LayoutJson.WidgetId(i) is { } wid && allowedSet.Contains(wid)).ToList();
            if (items.Count != beforeFilter) hasChanged = true;
            for (var i = 0; i < items.Count; i++) { items[i]["order"] = i; items[i]["priority"] = i; }

            var storedIds = stored.OfType<JsonObject>().Select(LayoutJson.ItemId).ToList();
            if (stored.Count != items.Count) hasChanged = true;
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var storedId = i < storedIds.Count ? storedIds[i] : null;
                    if (storedId != LayoutJson.ItemId(items[i])) { hasChanged = true; break; }
                }
            }
            layout.LayoutJson = LayoutJson.Serialize(items);
        }

        if (hasChanged)
        {
            layout.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var canConfigure = acl.IsSuperAdmin
            || OpenMercato.Modules.Auth.Services.FeatureMatch.HasFeature(acl.Features, "dashboards.configure");

        string? userName = null, userEmail = null, userLabel = null;
        var user = await db.Set<User>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        if (user is not null)
        {
            userName = encryption.Decrypt(user.Name)?.Trim();
            userEmail = encryption.Decrypt(user.Email);
            userLabel = !string.IsNullOrEmpty(userName) ? userName : userEmail;
        }
        userLabel ??= userId.ToString();

        var itemsArr = new JsonArray();
        foreach (var it in items) itemsArr.Add(it.DeepClone());
        var allowedArr = new JsonArray();
        foreach (var id in allowedIds) allowedArr.Add(id);
        var widgetsArr = new JsonArray();
        foreach (var w in allowedWidgets) widgetsArr.Add(WidgetCatalog.ToSummary(w));

        var response = new JsonObject
        {
            ["layout"] = new JsonObject { ["items"] = itemsArr },
            ["allowedWidgetIds"] = allowedArr,
            ["canConfigure"] = canConfigure,
            ["context"] = new JsonObject
            {
                ["userId"] = userId.ToString(),
                ["tenantId"] = tenantId?.ToString(),
                ["organizationId"] = organizationId?.ToString(),
                ["userName"] = userName,
                ["userEmail"] = userEmail,
                ["userLabel"] = userLabel,
            },
            ["widgets"] = widgetsArr,
        };
        return DashboardRouteHelpers.JsonContent(response);
    }

    private static async Task<IResult> PutAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var userId = auth.UserId;
        var tenantId = auth.TenantId;
        var organizationId = auth.OrganizationId;

        var body = await DashboardRouteHelpers.ReadJsonAsync(http);
        if (!body.Parsed)
            return Results.Json(new { error = "Invalid JSON body" }, statusCode: 400);

        // Validate dashboardLayoutSchema: { items: LayoutItem[] }.
        var issues = new JsonArray();
        var itemsNode = (body.Node as JsonObject)?["items"] as JsonArray;
        if (itemsNode is null)
            return Results.Json(new { error = "Invalid layout payload", issues = Array.Empty<object>() }, statusCode: 400);

        var parsedItems = new List<JsonObject>();
        var index = 0;
        foreach (var el in itemsNode)
        {
            if (el is not JsonObject item) { AddIssue(issues, $"items[{index}]", "Expected object"); index++; continue; }
            var id = DashboardRouteHelpers.Str(item["id"]);
            var widgetId = DashboardRouteHelpers.Str(item["widgetId"]);
            var order = DashboardRouteHelpers.Int(item["order"]);
            var priority = DashboardRouteHelpers.Int(item["priority"]);
            var size = DashboardRouteHelpers.Str(item["size"]);
            if (id is null || !System.Guid.TryParse(id, out _)) AddIssue(issues, $"items[{index}].id", "Invalid uuid");
            if (string.IsNullOrEmpty(widgetId)) AddIssue(issues, $"items[{index}].widgetId", "Required, min length 1");
            if (order is null || order < 0) AddIssue(issues, $"items[{index}].order", "Required int >= 0");
            if (item["priority"] is not null && (priority is null || priority < 0)) AddIssue(issues, $"items[{index}].priority", "int >= 0");
            if (size is not null && size is not ("sm" or "md" or "lg")) AddIssue(issues, $"items[{index}].size", "Invalid size");
            if (id is not null && !string.IsNullOrEmpty(widgetId))
            {
                var obj = new JsonObject { ["id"] = id, ["widgetId"] = widgetId, ["size"] = size ?? DefaultSize };
                if (item.TryGetPropertyValue("settings", out var settings)) obj["settings"] = settings?.DeepClone();
                parsedItems.Add(obj);
            }
            index++;
        }
        if (issues.Count > 0)
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid layout payload", ["issues"] = issues }, 400);

        var acl = await rbac.LoadAcl(userId, tenantId, organizationId);
        var widgets = WidgetCatalog.LoadAll();
        var allowedIds = await WidgetAccess.ResolveAllowedWidgetIds(db,
            new WidgetAccessContext(userId, tenantId, organizationId, acl.Features, acl.IsSuperAdmin), widgets);
        var allowedSet = allowedIds.ToHashSet();

        // Re-index and drop items whose widget is not allowed.
        var sanitized = new List<JsonObject>();
        var idx2 = 0;
        foreach (var item in parsedItems)
        {
            var widgetId = LayoutJson.WidgetId(item)!;
            if (!allowedSet.Contains(widgetId)) continue;
            item["order"] = idx2;
            item["priority"] = idx2;
            sanitized.Add(item);
            idx2++;
        }

        var uniqueIds = sanitized.Select(LayoutJson.ItemId).ToHashSet();
        if (uniqueIds.Count != sanitized.Count)
            return Results.Json(new { error = "Layout item IDs must be unique" }, statusCode: 400);

        var layout = await db.Set<DashboardLayout>()
            .FirstOrDefaultAsync(l => l.UserId == userId && l.TenantId == tenantId
                && l.OrganizationId == organizationId && l.DeletedAt == null);
        if (layout is null)
        {
            layout = new DashboardLayout
            {
                Id = System.Guid.NewGuid(),
                UserId = userId,
                TenantId = tenantId,
                OrganizationId = organizationId,
                LayoutJson = LayoutJson.Serialize(sanitized),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Set<DashboardLayout>().Add(layout);
        }
        else
        {
            layout.LayoutJson = LayoutJson.Serialize(sanitized);
            layout.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();

        return Results.Json(new { ok = true });
    }

    private static void AddIssue(JsonArray issues, string path, string message) =>
        issues.Add(new JsonObject { ["path"] = path, ["message"] = message });
}
