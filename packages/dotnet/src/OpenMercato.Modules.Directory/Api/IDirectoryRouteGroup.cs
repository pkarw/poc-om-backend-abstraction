using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// A group of related directory HTTP routes (one per upstream api/&lt;area&gt; file). Parallels the
/// auth module's IAuthRouteGroup pattern: <see cref="DirectoryModule.MapRoutes"/> discovers every
/// implementation in the Directory assembly via reflection and calls <see cref="Map"/>, so new
/// route files require no edits to DirectoryModule. Implementations need a public parameterless ctor.
/// </summary>
public interface IDirectoryRouteGroup
{
    void Map(IEndpointRouteBuilder routes);
}
