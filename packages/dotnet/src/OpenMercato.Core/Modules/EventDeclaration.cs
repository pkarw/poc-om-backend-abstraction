namespace OpenMercato.Core.Modules;

/// <summary>
/// A typed event a module declares, mirroring upstream <c>events.ts</c>
/// (<c>createModuleEvents({ moduleId, events })</c> — the declared, named events
/// with their payload shapes). Declaring an event names it and documents its
/// payload; the event bus itself is Core (<c>IEventBus</c>).
///
/// Note (from the auth port contract): some upstream events are declared but
/// never emitted (e.g. <c>auth.logout</c>). Declaration is the supported
/// surface; emission is a runtime concern of the module.
/// </summary>
/// <param name="Name">Dotted event id, e.g. <c>"auth.user.created"</c>. Unique across all modules.</param>
/// <param name="PayloadShape">
/// Human-readable documentation of the payload shape, e.g. <c>"{ id, organizationId, tenantId }"</c>.
/// </param>
/// <param name="Persistent">
/// Whether the event is persisted (durable) vs fire-and-forget, mirroring upstream's
/// <c>persistent</c> flag.
/// </param>
public record EventDeclaration(
    string Name,
    string? PayloadShape = null,
    bool Persistent = false);
