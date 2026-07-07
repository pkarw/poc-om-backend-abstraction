using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Dashboards.Services;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// POST /api/dashboards/widgets/data/batch — 1:1 port of upstream api/widgets/data/batch/route.ts.
/// Resolves up to 50 widget-data requests with one auth/RBAC/tenant setup; each item is resolved
/// independently with per-item error isolation. Auth (401) + analytics.view (403) enforced by the
/// filter.
///
/// PARITY-TODO: with the empty analytics registry each item fails validation, so results are
/// <c>{id, ok:false, error:"Invalid entity type: &lt;x&gt;"}</c> (HTTP 200 for the batch) — the exact
/// upstream behavior until sales/customers/catalog register entity configs.
/// </summary>
public sealed class WidgetsDataBatchRouteGroup : IDashboardRouteGroup
{
    private const int MaxBatchSize = 50;

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/dashboards/widgets/data/batch", PostAsync).RequireFeatures("analytics.view");
    }

    private static async Task<IResult> PostAsync(HttpContext http, IAnalyticsRegistry registry, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;

        var body = await DashboardRouteHelpers.ReadJsonAsync(http);
        if (!body.Parsed) return Results.Json(new { error = "Invalid JSON body" }, statusCode: 400);

        var requestsNode = body.AsObject?["requests"] as JsonArray;
        if (requestsNode is null || requestsNode.Count < 1 || requestsNode.Count > MaxBatchSize)
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid request payload", ["issues"] = new JsonArray() }, 400);

        var entries = new List<(string Id, WidgetDataRequest Request)>();
        foreach (var el in requestsNode)
        {
            var eo = el as JsonObject;
            var id = DashboardRouteHelpers.Str(eo?["id"]);
            var reqObj = eo?["request"] as JsonObject;
            if (string.IsNullOrEmpty(id) || reqObj is null)
                return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid request payload", ["issues"] = new JsonArray() }, 400);
            var parsed = DashboardRouteHelpers.ParseWidgetDataRequest(reqObj, out _);
            if (parsed is null)
                return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid request payload", ["issues"] = new JsonArray() }, 400);
            entries.Add((id, parsed));
        }

        if (auth.TenantId is null)
            return Results.Json(new { error = "Tenant context is required" }, statusCode: 400);

        var service = new WidgetDataService(registry);

        // resolveEntityFeatureAccess: union-check the gated entity types once, fall back per-entity.
        var access = new Dictionary<string, bool>();
        var featuresByEntity = new Dictionary<string, IReadOnlyList<string>>();
        var union = new HashSet<string>();
        foreach (var et in entries.Select(e => e.Request.EntityType).Distinct())
        {
            var features = registry.GetRequiredFeatures(et) ?? Array.Empty<string>();
            featuresByEntity[et] = features;
            if (features.Count == 0) access[et] = true;
            else foreach (var f in features) union.Add(f);
        }
        var gated = featuresByEntity.Where(kv => kv.Value.Count > 0).ToList();
        if (gated.Count > 0)
        {
            if (await rbac.UserHasAllFeatures(auth.UserId, union.ToList(), auth.TenantId, auth.OrganizationId))
                foreach (var (et, _) in gated) access[et] = true;
            else
                foreach (var (et, features) in gated)
                    access[et] = await rbac.UserHasAllFeatures(auth.UserId, features, auth.TenantId, auth.OrganizationId);
        }

        var results = new JsonArray();
        foreach (var (id, request) in entries)
        {
            if (access.TryGetValue(request.EntityType, out var allowed) && allowed == false)
            {
                results.Add(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "Forbidden" });
                continue;
            }
            try
            {
                var data = service.FetchWidgetData(request);
                results.Add(new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = data });
            }
            catch (WidgetDataValidationError ex)
            {
                results.Add(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.Message });
            }
            catch
            {
                results.Add(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "An error occurred while processing your request" });
            }
        }

        return DashboardRouteHelpers.JsonContent(new JsonObject { ["results"] = results });
    }
}
