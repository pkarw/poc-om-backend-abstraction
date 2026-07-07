using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// PATCH /api/dashboards/layout/{itemId} — 1:1 port of upstream api/layout/[itemId]/route.ts.
/// Updates the size/settings of a single item inside the caller's layout. Note (per the contract):
/// the layout-item mutation is PATCH, NOT DELETE — the client removes a widget by re-sending the
/// full list via PUT /layout. Auth (401) + dashboards.configure (403) enforced by the filter.
/// </summary>
public sealed class LayoutItemRouteGroup : IDashboardRouteGroup
{
    private const string DefaultSize = "md";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPatch("/api/dashboards/layout/{itemId}", PatchAsync).RequireFeatures("dashboards.configure");
    }

    private static async Task<IResult> PatchAsync(HttpContext http, string itemId, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        if (string.IsNullOrEmpty(itemId))
            return Results.Json(new { error = "Missing layout item id" }, statusCode: 400);

        var body = await DashboardRouteHelpers.ReadJsonAsync(http);
        if (!body.Parsed)
            return Results.Json(new { error = "Invalid JSON body" }, statusCode: 400);
        if (body.Node is not JsonObject obj)
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);

        // dashboardLayoutItemPatchSchema: { id (injected), size? enum, settings? }.
        var size = DashboardRouteHelpers.Str(obj["size"]);
        if (obj["size"] is not null && size is not ("sm" or "md" or "lg"))
            return DashboardRouteHelpers.JsonContent(new JsonObject
            {
                ["error"] = "Invalid payload",
                ["issues"] = new JsonArray { new JsonObject { ["path"] = "size", ["message"] = "Invalid size" } },
            }, 400);

        var layout = await db.Set<DashboardLayout>()
            .FirstOrDefaultAsync(l => l.UserId == auth.UserId && l.TenantId == auth.TenantId
                && l.OrganizationId == auth.OrganizationId && l.DeletedAt == null);
        if (layout is null)
            return Results.Json(new { error = "Layout not found" }, statusCode: 404);

        var arr = LayoutJson.Parse(layout.LayoutJson);
        var idx = -1;
        for (var i = 0; i < arr.Count; i++)
            if (arr[i] is JsonObject o && LayoutJson.ItemId(o) == itemId) { idx = i; break; }
        if (idx == -1)
            return Results.Json(new { error = "Layout item not found" }, statusCode: 404);

        var current = (JsonObject)arr[idx]!;
        var currentSize = DashboardRouteHelpers.Str(current["size"]);
        var updated = new JsonObject();
        foreach (var kv in current) updated[kv.Key] = kv.Value?.DeepClone();
        updated["size"] = size ?? currentSize ?? DefaultSize;
        if (obj.TryGetPropertyValue("settings", out var newSettings) && newSettings is not null)
            updated["settings"] = newSettings.DeepClone();
        arr[idx] = updated;

        layout.LayoutJson = arr.ToJsonString();
        layout.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return Results.Json(new { ok = true });
    }
}
