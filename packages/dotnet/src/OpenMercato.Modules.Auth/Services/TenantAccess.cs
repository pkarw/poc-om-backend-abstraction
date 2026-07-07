using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Tenant-selection guards — a 1:1 port of upstream
/// packages/core/src/modules/auth/lib/tenantAccess.ts (resolveIsSuperAdmin, enforceTenantSelection).
/// </summary>
public static class TenantAccess
{
    public static async Task<bool> ResolveIsSuperAdmin(IRbacService rbac, AuthContext auth)
    {
        var acl = await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId);
        return acl.IsSuperAdmin;
    }

    /// <summary>
    /// Resolve the effective tenant for a mutation. <paramref name="provided"/> distinguishes an
    /// omitted field (undefined) from an explicit value; a non-super-admin may only target their own
    /// tenant, else 403 <c>{"error":"Not authorized to target this tenant."}</c>.
    /// </summary>
    public static Guid? EnforceTenantSelection(
        bool isSuperAdmin, Guid? actorTenant, bool provided, Guid? requestedValue)
    {
        if (isSuperAdmin)
            return provided ? requestedValue : actorTenant;

        if (actorTenant is null)
        {
            if (provided && requestedValue is not null)
                throw AuthHttpException.Forbidden("Not authorized to target this tenant.");
            return actorTenant;
        }

        if (!provided) return actorTenant;
        if (requestedValue == actorTenant) return actorTenant;
        throw AuthHttpException.Forbidden("Not authorized to target this tenant.");
    }
}
