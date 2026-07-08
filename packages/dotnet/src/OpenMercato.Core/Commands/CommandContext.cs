namespace OpenMercato.Core.Commands;

/// <summary>
/// The runtime context threaded through every command execution — the port of upstream
/// <c>CommandRuntimeContext</c> (packages/shared/src/lib/commands/types.ts) combined with the
/// resolved <c>OrganizationScope</c> (scope.ts). Built by the HTTP/CRUD layer from the request's
/// <c>AuthContext</c> + resolved org scope, and by CLI/worker callers for system writes.
///
/// Org-scope semantics mirror upstream and MUST be preserved end to end (spec 03 R20/R26):
///   - <see cref="OrganizationIds"/> == null  → no org restriction (super admin / all orgs)
///   - <see cref="OrganizationIds"/> == []     → access nothing (fail closed)
///   - <see cref="AllowedOrganizationIds"/> == null → unrestricted allowed set (super admin passes)
///
/// Intentionally free of any dependency on the Auth module (Core is referenced BY Auth, not the
/// other way round): the route layer maps <c>AuthContext</c> → <see cref="CommandContext"/>.
/// </summary>
public sealed record CommandContext
{
    /// <summary>Actor tenant (JWT <c>tenantId</c>). Null for unscoped system contexts.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>The selected organization for this request (upstream <c>selectedOrganizationId</c>).</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Acting user id (JWT <c>sub</c>). Null for system/CLI invocations.</summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// The org filter set for reads/writes (upstream <c>organizationIds</c>): null = unrestricted,
    /// empty = nothing. Expanded with org-tree descendants by the scope resolver upstream.
    /// </summary>
    public IReadOnlyList<Guid>? OrganizationIds { get; init; }

    /// <summary>
    /// The full set of organizations the actor may act on (upstream <c>organizationScope.allowedIds</c>).
    /// null = unrestricted. Used by <see cref="CommandScope"/> guards.
    /// </summary>
    public IReadOnlyList<Guid>? AllowedOrganizationIds { get; init; }

    /// <summary>True for verified super admins — bypasses org-scope guards (spec 03 R26).</summary>
    public bool IsSuperAdmin { get; init; }

    /// <summary>Trusted server-side invocation (CLI seeding, tenant setup) with no end-user actor.</summary>
    public bool SystemActor { get; init; }

    /// <summary>Optional origin tag propagated to emitted side effects (upstream <c>syncOrigin</c>).</summary>
    public string? SyncOrigin { get; init; }

    /// <summary>
    /// Request headers, when this command runs on an HTTP path. Read by the optimistic-lock helper
    /// (<c>x-om-ext-optimistic-lock-expected-updated-at</c>) and any extension-header consumer.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
