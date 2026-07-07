using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Authorization engine — a 1:1 port of upstream
/// packages/core/src/modules/auth/services/rbacService.ts. Resolves the effective ACL for a user
/// in a tenant/org scope and answers <c>userHasAllFeatures</c> with wildcard-aware matching.
///
/// Parity notes / documented deviations (see docs/decisions/0008-auth-rbac-cache-noop.md):
/// - Caching: upstream layers a 5-minute CacheStrategy with tag invalidation. The port evaluates
///   against the shared AppDbContext on every call (correct, uncached). The invalidate* methods are
///   retained as no-ops so callers compose unchanged. Boolean/ACL results are byte-identical.
/// - api_key:&lt;id&gt; subjects depend on the unported api_keys module and are treated as
///   "no grants" (// PARITY-TODO). Every Guid-keyed staff subject path is faithful.
/// - enabled-modules filtering (getEnabledModuleIds/filterGrantsByEnabledModules) is a
///   registry concern outside this port's scope; grants are matched as-is.
/// </summary>
public sealed class RbacService : IRbacService
{
    private readonly AppDbContext _db;

    public RbacService(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<bool> UserHasAllFeatures(
        Guid userId,
        IReadOnlyList<string> features,
        Guid? tenantId,
        Guid? organizationId)
    {
        if (features.Count == 0) return true;
        var acl = await LoadAcl(userId, tenantId, organizationId);
        if (acl.IsSuperAdmin) return true;
        if (acl.Organizations is not null
            && organizationId is { } org
            && !acl.Organizations.Contains(org.ToString())
            && !acl.Organizations.Contains("__all__"))
        {
            return false;
        }
        return FeatureMatch.HasAll(features, acl.Features);
    }

    /// <inheritdoc />
    public async Task<Acl> LoadAcl(Guid userId, Guid? tenantId, Guid? organizationId)
    {
        // Global super admin wins before the tenant requirement: a superadmin role/user ACL grants
        // '*' with no org restriction regardless of the requested scope (upstream isGlobalSuperAdmin).
        if (await IsGlobalSuperAdmin(userId))
            return new Acl { IsSuperAdmin = true, Features = new[] { "*" }, Organizations = null };

        var user = await _db.Set<User>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null);
        if (user is null)
            return new Acl { IsSuperAdmin = false, Features = Array.Empty<string>(), Organizations = null };

        var tid = tenantId ?? user.TenantId;
        if (tid is null)
            return new Acl { IsSuperAdmin = false, Features = Array.Empty<string>(), Organizations = null };

        // Per-user ACL wins EXCLUSIVELY over role aggregation.
        var uacl = await _db.Set<UserAcl>().AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == tid && a.DeletedAt == null);
        if (uacl is not null)
        {
            return new Acl
            {
                IsSuperAdmin = uacl.IsSuperAdmin,
                Features = JsonArray.Parse(uacl.FeaturesJson) ?? Array.Empty<string>(),
                Organizations = JsonArray.Parse(uacl.OrganizationsJson),
            };
        }

        // Aggregate role ACLs for the roles the user holds within this tenant.
        var roleIds = await RoleIdsForUserInTenant(userId, tid.Value);
        var isSuper = false;
        var features = new List<string>();
        string[]? organizations = Array.Empty<string>();
        if (roleIds.Count > 0)
        {
            var racls = await _db.Set<RoleAcl>().AsNoTracking()
                .Where(a => a.TenantId == tid && roleIds.Contains(a.RoleId) && a.DeletedAt == null)
                .ToListAsync();
            foreach (var r in racls)
            {
                isSuper = isSuper || r.IsSuperAdmin;
                var rf = JsonArray.Parse(r.FeaturesJson);
                if (rf is not null)
                    foreach (var f in rf)
                        if (!features.Contains(f)) features.Add(f);

                if (organizations is not null)
                {
                    var ro = JsonArray.Parse(r.OrganizationsJson);
                    if (ro is null) organizations = null;
                    else if (ro.Contains("__all__")) organizations = null;
                    else organizations = organizations.Concat(ro).Distinct().ToArray();
                }
            }
        }

        return new Acl { IsSuperAdmin = isSuper, Features = features.ToArray(), Organizations = organizations };
    }

    /// <summary>Raw granted feature strings for a scope (upstream getGrantedFeatures).</summary>
    public async Task<string[]> GetGrantedFeatures(Guid userId, Guid? tenantId, Guid? organizationId)
    {
        var acl = await LoadAcl(userId, tenantId, organizationId);
        return acl.Features;
    }

    /// <summary>
    /// Effective super-admin for a user irrespective of scope: a user-level super-admin ACL, OR any
    /// role the user holds carrying a super-admin RoleAcl (upstream isGlobalSuperAdmin).
    /// </summary>
    public async Task<bool> IsGlobalSuperAdmin(Guid userId)
    {
        var userSuper = await _db.Set<UserAcl>().AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.IsSuperAdmin && a.DeletedAt == null);
        if (userSuper) return true;

        var roleIds = await _db.Set<UserRole>().AsNoTracking()
            .Where(l => l.UserId == userId && l.DeletedAt == null)
            .Select(l => l.RoleId)
            .Distinct()
            .ToListAsync();
        if (roleIds.Count == 0) return false;

        return await _db.Set<RoleAcl>().AsNoTracking()
            .AnyAsync(a => a.IsSuperAdmin && roleIds.Contains(a.RoleId) && a.DeletedAt == null);
    }

    private async Task<List<Guid>> RoleIdsForUserInTenant(Guid userId, Guid tenantId)
    {
        // UserRole has no tenant column; upstream scopes by the joined role's tenant.
        var query =
            from link in _db.Set<UserRole>().AsNoTracking()
            join role in _db.Set<Role>().AsNoTracking() on link.RoleId equals role.Id
            where link.UserId == userId && link.DeletedAt == null
                  && role.TenantId == tenantId && role.DeletedAt == null
            select role.Id;
        return await query.Distinct().ToListAsync();
    }

    // Cache invalidation hooks — no-ops in the port (see class-level parity note). Retained so that
    // route handlers mirroring upstream invalidation calls compose without change.
    public Task InvalidateUserCache(Guid userId) => Task.CompletedTask;
    public Task InvalidateTenantCache(Guid tenantId) => Task.CompletedTask;
    public Task InvalidateOrganizationCache(Guid organizationId) => Task.CompletedTask;
    public Task InvalidateAllCache() => Task.CompletedTask;
}
