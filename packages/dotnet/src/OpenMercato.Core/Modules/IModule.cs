using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OpenMercato.Core.Modules;

/// <summary>
/// A backend module, mirroring upstream packages/core/src/modules/&lt;module&gt;/.
/// One implementation per module contributes everything the upstream file
/// conventions contribute:
///   - MapRoutes        -> api/&lt;method&gt;/&lt;path&gt;.ts
///   - ConfigureModel   -> data/entities.ts (MikroORM)
///   - ConfigureServices-> di.ts (Awilix) + registers IJobHandler (workers/*.ts)
///                         and IEventSubscriber (subscribers/*.ts) implementations
///   - AclFeatures      -> acl.ts
/// </summary>
public interface IModule
{
    /// <summary>Module id, snake_case, identical to upstream (e.g. "health_check").</summary>
    string Id { get; }

    /// <summary>Feature flags declared by the module (upstream acl.ts).</summary>
    IReadOnlyList<string> AclFeatures { get; }

    /// <summary>Register module services, job handlers and event subscribers (upstream di.ts).</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>Map module entities onto the shared DbContext (upstream data/entities.ts).</summary>
    void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>Map HTTP routes under /api/&lt;module_id&gt;/... (upstream api/&lt;method&gt;/&lt;path&gt;.ts).</summary>
    void MapRoutes(IEndpointRouteBuilder routes);
}
