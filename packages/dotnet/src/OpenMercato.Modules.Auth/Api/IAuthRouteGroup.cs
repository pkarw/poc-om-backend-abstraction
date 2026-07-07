using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// A group of related auth HTTP routes (one per upstream api/&lt;area&gt; file). Domain slices
/// implement this; <see cref="AuthModule.MapRoutes"/> discovers every implementation in the Auth
/// assembly via reflection and calls <see cref="Map"/>, so new route files require no edits to
/// AuthModule. Implementations must have a public parameterless constructor.
/// </summary>
public interface IAuthRouteGroup
{
    void Map(IEndpointRouteBuilder routes);
}
