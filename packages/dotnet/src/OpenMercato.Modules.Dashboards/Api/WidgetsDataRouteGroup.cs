using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Dashboards.Services;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// POST /api/dashboards/widgets/data — 1:1 port of upstream api/widgets/data/route.ts. Generic
/// aggregation endpoint. Auth (401) + analytics.view (403) enforced by the filter. Per-entity feature
/// gating uses the analytics registry (empty in this port), then a tenant guard, then the widget-data
/// service.
///
/// PARITY-TODO: the analytics registry is empty (sales/customers/catalog not ported), so any real
/// request rejects with 400 "Invalid entity type: &lt;x&gt;" — the endpoint responds 200-shaped only
/// once a domain module registers an entity config. Org-scope resolution
/// (resolveOrganizationScopeForRequest) and the API interceptor runner are simplified no-op
/// passthroughs (PARITY-TODO) until directory's org-scope util and the interceptor framework are ported.
/// </summary>
public sealed class WidgetsDataRouteGroup : IDashboardRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/dashboards/widgets/data", PostAsync).RequireFeatures("analytics.view");
    }

    private static async Task<IResult> PostAsync(HttpContext http, IAnalyticsRegistry registry, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;

        var body = await DashboardRouteHelpers.ReadJsonAsync(http);
        if (!body.Parsed) return Results.Json(new { error = "Invalid JSON body" }, statusCode: 400);

        var request = DashboardRouteHelpers.ParseWidgetDataRequest(body.AsObject, out var issues);
        if (request is null)
            return DashboardRouteHelpers.JsonContent(new JsonObject { ["error"] = "Invalid request payload", ["issues"] = issues }, 400);

        var entityFeatures = registry.GetRequiredFeatures(request.EntityType);
        if (entityFeatures is { Count: > 0 })
        {
            var ok = await rbac.UserHasAllFeatures(auth.UserId, entityFeatures, auth.TenantId, auth.OrganizationId);
            if (!ok) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
        }

        if (auth.TenantId is null)
            return Results.Json(new { error = "Tenant context is required" }, statusCode: 400);

        // PARITY-TODO: org-scope resolution + runApiInterceptorsBefore are no-op passthroughs here.
        try
        {
            var service = new WidgetDataService(registry);
            var result = service.FetchWidgetData(request);
            return DashboardRouteHelpers.JsonContent(result);
        }
        catch (WidgetDataValidationError ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
        catch
        {
            return Results.Json(new { error = "An error occurred while processing your request" }, statusCode: 500);
        }
    }
}
