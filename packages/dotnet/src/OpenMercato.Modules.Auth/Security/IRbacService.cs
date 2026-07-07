namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// A resolved ACL for a subject (spec 05 R29). <see cref="Organizations"/> null means all orgs.
/// </summary>
public sealed record Acl
{
    public bool IsSuperAdmin { get; init; }
    public string[] Features { get; init; } = Array.Empty<string>();
    /// <summary>List of allowed organization ids; null = all organizations.</summary>
    public string[]? Organizations { get; init; }
}

/// <summary>
/// Authorization engine (upstream RbacService). Implemented by the domain slice (agent B2)
/// and registered by the integrator in AuthModule.ConfigureServices. The foundation route
/// filters (<see cref="EndpointAuthExtensions.RequireFeatures"/>) resolve this from DI.
/// </summary>
public interface IRbacService
{
    /// <summary>
    /// True when the user is granted every feature in <paramref name="features"/> within the
    /// given tenant/org scope (spec 05 R27-R31). Empty list => true.
    /// </summary>
    Task<bool> UserHasAllFeatures(
        Guid userId,
        IReadOnlyList<string> features,
        Guid? tenantId,
        Guid? organizationId);

    /// <summary>Resolve the effective ACL for a user in a tenant/org scope (spec 05 R29).</summary>
    Task<Acl> LoadAcl(Guid userId, Guid? tenantId, Guid? organizationId);

    /// <summary>Invalidate any cached ACL for a user (upstream rbacService cache tags).</summary>
    Task InvalidateUserCache(Guid userId);

    /// <summary>Invalidate any cached ACLs scoped to a tenant.</summary>
    Task InvalidateTenantCache(Guid tenantId);

    /// <summary>Invalidate any cached ACLs scoped to an organization.</summary>
    Task InvalidateOrganizationCache(Guid organizationId);

    /// <summary>Invalidate the entire ACL cache.</summary>
    Task InvalidateAllCache();
}
