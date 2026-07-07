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
///
/// The declaration members below mirror the rest of upstream's per-module
/// surface (notifications.ts, events.ts, ce.ts / data/fields.ts, the richer
/// acl.ts titles). They are OPTIONAL: C# default interface implementations mean
/// existing modules compile unchanged and opt in only where they have something
/// to declare.
/// </summary>
public interface IModule
{
    /// <summary>Module id, snake_case, identical to upstream (e.g. "health_check").</summary>
    string Id { get; }

    /// <summary>Feature flags declared by the module (upstream acl.ts). Kept for back-compat.</summary>
    IReadOnlyList<string> AclFeatures { get; }

    /// <summary>Register module services, job handlers and event subscribers (upstream di.ts).</summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>Map module entities onto the shared DbContext (upstream data/entities.ts).</summary>
    void ConfigureModel(ModelBuilder modelBuilder);

    /// <summary>Map HTTP routes under /api/&lt;module_id&gt;/... (upstream api/&lt;method&gt;/&lt;path&gt;.ts).</summary>
    void MapRoutes(IEndpointRouteBuilder routes);

    // --- Optional declaration surface (upstream parity) -------------------------------

    /// <summary>
    /// Richer RBAC feature declarations with titles (upstream acl.ts).
    /// Defaults to deriving <c>{ id, title = id }</c> from <see cref="AclFeatures"/> so
    /// modules that only declare bare ids still surface here.
    /// </summary>
    IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions =>
        AclFeatures.Select(f => new AclFeatureDefinition(f, f)).ToList();

    /// <summary>Notification types the module declares (upstream notifications.ts). Empty by default.</summary>
    IReadOnlyList<NotificationTypeDefinition> NotificationTypes =>
        Array.Empty<NotificationTypeDefinition>();

    /// <summary>Custom-field sets the module attaches to entities (upstream ce.ts / data/fields.ts). Empty by default.</summary>
    IReadOnlyList<CustomFieldSet> CustomFieldSets =>
        Array.Empty<CustomFieldSet>();

    /// <summary>Typed events the module declares (upstream events.ts). Empty by default.</summary>
    IReadOnlyList<EventDeclaration> DeclaredEvents =>
        Array.Empty<EventDeclaration>();
}
