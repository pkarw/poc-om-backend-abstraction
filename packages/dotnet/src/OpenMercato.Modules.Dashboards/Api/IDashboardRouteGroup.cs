using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>
/// A group of related dashboards HTTP routes (one per upstream api/&lt;area&gt; file). Mirrors the
/// auth/directory <c>IAuthRouteGroup</c>/<c>IDirectoryRouteGroup</c> pattern:
/// <see cref="DashboardsModule.MapRoutes"/> discovers every implementation in the Dashboards
/// assembly via reflection and calls <see cref="Map"/>, so new route files need no edits to the
/// module. Implementations require a public parameterless ctor.
/// </summary>
public interface IDashboardRouteGroup
{
    void Map(IEndpointRouteBuilder routes);
}
