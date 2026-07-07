using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Dashboards.Lib;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// GET /api/dashboards/widgets/catalog — 1:1 port of upstream api/widgets/catalog.ts. Lists the full
/// widget catalog for the admin visibility editor. Auth (401) + dashboards.admin.assign-widgets (403)
/// enforced by the filter.
/// </summary>
public sealed class WidgetsCatalogRouteGroup : IDashboardRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dashboards/widgets/catalog", GetAsync)
            .RequireFeatures("dashboards.admin.assign-widgets");
    }

    private static IResult GetAsync(HttpContext http)
    {
        var items = new JsonArray();
        foreach (var w in WidgetCatalog.LoadAll()) items.Add(WidgetCatalog.ToSummary(w));
        return DashboardRouteHelpers.JsonContent(new JsonObject { ["items"] = items });
    }
}
