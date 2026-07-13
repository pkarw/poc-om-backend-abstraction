using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// A group of related catalog HTTP routes (one per upstream api/&lt;area&gt; file). Domain slices
/// implement this; <see cref="CatalogModule.MapRoutes"/> discovers every implementation in the Catalog
/// assembly via reflection and calls <see cref="Map"/>, so new route files require no edits to
/// CatalogModule. Implementations must have a public parameterless constructor. Parallel to customers'
/// <c>ICustomersRouteGroup</c>.
/// </summary>
public interface ICatalogRouteGroup
{
    void Map(IEndpointRouteBuilder routes);
}
