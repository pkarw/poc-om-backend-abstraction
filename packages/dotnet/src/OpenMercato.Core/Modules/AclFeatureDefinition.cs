namespace OpenMercato.Core.Modules;

/// <summary>
/// A RBAC feature a module declares, mirroring upstream <c>acl.ts</c>
/// (each entry is a <c>{ id, title, module }</c> feature declaration; the
/// module is implied by the owning <see cref="IModule"/>).
///
/// This is the richer form of the legacy <see cref="IModule.AclFeatures"/>
/// (bare id strings): it carries the human-readable title upstream exposes via
/// <c>GET /api/auth/features</c>. The bare-string list is kept for back-compat.
/// </summary>
/// <param name="Id">Dotted feature id, e.g. <c>"auth.users.list"</c>.</param>
/// <param name="Title">Human-readable title, e.g. <c>"List users"</c>.</param>
public record AclFeatureDefinition(string Id, string Title);
