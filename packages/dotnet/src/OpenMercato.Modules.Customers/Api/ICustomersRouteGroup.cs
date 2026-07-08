using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// A group of related customers HTTP routes (one per upstream api/&lt;area&gt; file). Domain slices
/// implement this; <see cref="CustomersModule.MapRoutes"/> discovers every implementation in the
/// Customers assembly via reflection and calls <see cref="Map"/>, so new route files require no
/// edits to CustomersModule. Implementations must have a public parameterless constructor.
/// Parallel to auth's <c>IAuthRouteGroup</c>.
/// </summary>
public interface ICustomersRouteGroup
{
    void Map(IEndpointRouteBuilder routes);
}
