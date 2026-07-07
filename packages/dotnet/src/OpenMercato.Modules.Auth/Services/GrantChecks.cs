using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Privilege-escalation guards — a 1:1 port of upstream
/// packages/core/src/modules/auth/lib/grantChecks.ts. Each guard throws <see cref="AuthHttpException"/>
/// (upstream CrudHttpError/forbidden) on denial; route handlers translate that to the HTTP response.
/// </summary>
public static class GrantChecks
{
    public sealed record ActorAcl(bool IsSuperAdmin, string[] Features, string[]? Organizations);

    // ---- feature/list normalization (upstream normalizeStringList) ----------------------------

    public static string[] NormalizeGrantFeatureList(IEnumerable<string>? values)
    {
        if (values is null) return Array.Empty<string>();
        var seen = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (set.Add(trimmed)) seen.Add(trimmed);
        }
        return seen.ToArray();
    }

    // ---- actor ACL ----------------------------------------------------------------------------

    private static async Task<ActorAcl> LoadActorAcl(
        IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId)
    {
        if (actorUserId is null)
            throw AuthHttpException.Forbidden("Not authorized to grant ACL privileges.");
        var acl = await rbac.LoadAcl(actorUserId.Value, tenantId, organizationId);
        return new ActorAcl(
            acl.IsSuperAdmin,
            NormalizeGrantFeatureList(acl.Features),
            acl.Organizations is null ? null : NormalizeGrantFeatureList(acl.Organizations));
    }

    // ---- super-admin evaluation (DB) ----------------------------------------------------------

    public static async Task<bool> IsUserEffectivelySuperAdmin(AppDbContext db, Guid userId)
    {
        var direct = await db.Set<UserAcl>().AsNoTracking()
            .AnyAsync(a => a.UserId == userId && a.IsSuperAdmin && a.DeletedAt == null);
        if (direct) return true;
        var roleIds = await db.Set<UserRole>().AsNoTracking()
            .Where(l => l.UserId == userId && l.DeletedAt == null)
            .Select(l => l.RoleId).Distinct().ToListAsync();
        if (roleIds.Count == 0) return false;
        return await db.Set<RoleAcl>().AsNoTracking()
            .AnyAsync(a => a.IsSuperAdmin && roleIds.Contains(a.RoleId) && a.DeletedAt == null);
    }

    public static Task<bool> IsRoleEffectivelySuperAdmin(AppDbContext db, Guid roleId) =>
        db.Set<RoleAcl>().AsNoTracking()
            .AnyAsync(a => a.RoleId == roleId && a.IsSuperAdmin && a.DeletedAt == null);

    public static async Task<HashSet<Guid>> ListSuperAdminUserIds(AppDbContext db, Guid? tenantId)
    {
        var ids = new HashSet<Guid>();
        var userAclQuery = db.Set<UserAcl>().AsNoTracking().Where(a => a.IsSuperAdmin && a.DeletedAt == null);
        if (tenantId is { } tid) userAclQuery = userAclQuery.Where(a => a.TenantId == tid);
        foreach (var uid in await userAclQuery.Select(a => a.UserId).ToListAsync()) ids.Add(uid);

        var roleIds = await db.Set<RoleAcl>().AsNoTracking()
            .Where(a => a.IsSuperAdmin && a.DeletedAt == null)
            .Select(a => a.RoleId).Distinct().ToListAsync();
        if (roleIds.Count > 0)
        {
            var linkedUsers = await db.Set<UserRole>().AsNoTracking()
                .Where(l => roleIds.Contains(l.RoleId) && l.DeletedAt == null)
                .Select(l => l.UserId).ToListAsync();
            foreach (var uid in linkedUsers) ids.Add(uid);
        }
        return ids;
    }

    // ---- target guards ------------------------------------------------------------------------

    public static async Task AssertActorCanModifySuperAdminUserTarget(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        Guid targetUserId, bool? actorIsSuperAdmin = null)
    {
        var isSa = actorIsSuperAdmin ?? (await LoadActorAcl(rbac, actorUserId, tenantId, organizationId)).IsSuperAdmin;
        if (isSa) return;
        if (await IsUserEffectivelySuperAdmin(db, targetUserId))
            throw AuthHttpException.Forbidden("Only super administrators can modify super administrator accounts.");
    }

    public static async Task AssertActorCanModifySuperAdminRoleTarget(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        Guid targetRoleId, bool? actorIsSuperAdmin = null)
    {
        var isSa = actorIsSuperAdmin ?? (await LoadActorAcl(rbac, actorUserId, tenantId, organizationId)).IsSuperAdmin;
        if (isSa) return;
        if (await IsRoleEffectivelySuperAdmin(db, targetRoleId))
            throw AuthHttpException.Forbidden("Only super administrators can modify super administrator roles.");
    }

    public static async Task AssertActorCanAccessUserTarget(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        Guid targetUserId, bool? actorIsSuperAdmin = null)
    {
        var isSa = actorIsSuperAdmin ?? (await LoadActorAcl(rbac, actorUserId, tenantId, organizationId)).IsSuperAdmin;
        if (isSa) return;

        var target = await db.Set<User>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == targetUserId && u.DeletedAt == null);
        // Missing target: delegate (every call site is itself tenant-scoped).
        if (target is null) return;

        if (target.TenantId is null || target.TenantId != tenantId)
            throw new AuthHttpException(404, new { error = "User not found" });

        var actorAcl = await LoadActorAcl(rbac, actorUserId, tenantId, organizationId);
        if (actorAcl.Organizations is not null && !actorAcl.Organizations.Contains("__all__"))
        {
            var targetOrg = target.OrganizationId?.ToString();
            if (targetOrg is null || !actorAcl.Organizations.Contains(targetOrg))
                throw AuthHttpException.Forbidden("Not authorized to access this user.");
        }
    }

    public static async Task AssertActorCanAccessRoleTarget(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        Guid targetRoleId, bool? actorIsSuperAdmin = null)
    {
        var isSa = actorIsSuperAdmin ?? (await LoadActorAcl(rbac, actorUserId, tenantId, organizationId)).IsSuperAdmin;
        if (isSa) return;

        var target = await db.Set<Role>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == targetRoleId && r.DeletedAt == null);
        if (target is null) return;
        if (target.TenantId != tenantId)
            throw new AuthHttpException(404, new { error = "Role not found" });
    }

    // ---- ACL grant guards ---------------------------------------------------------------------

    public static async Task AssertActorCanGrantAcl(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        bool isSuperAdmin, string[] features, string[]? organizations, bool organizationsProvided = true)
    {
        var actorAcl = await LoadActorAcl(rbac, actorUserId, tenantId, organizationId);
        if (actorAcl.IsSuperAdmin) return;
        if (tenantId is null)
            throw AuthHttpException.Forbidden("Tenant context is required to grant ACL features.");
        AssertGrantSnapshot(actorAcl, isSuperAdmin, features, organizations, organizationsProvided);
    }

    /// <summary>Resolve role tokens (uuid or name) and assert the actor may grant each (upstream assertActorCanGrantRoleTokens).</summary>
    public static async Task<List<Role>> AssertActorCanGrantRoleTokens(
        AppDbContext db, IRbacService rbac, Guid? actorUserId, Guid? tenantId, Guid? organizationId,
        IEnumerable<string>? roleTokens)
    {
        var tokens = NormalizeGrantFeatureList(roleTokens);
        if (tokens.Length == 0) return new List<Role>();

        var roles = await ResolveRolesForGrant(db, tokens, tenantId);

        var actorAcl = await LoadActorAcl(rbac, actorUserId, tenantId, organizationId);
        if (actorAcl.IsSuperAdmin) return roles;
        if (tenantId is null)
            throw AuthHttpException.Forbidden("Tenant context is required to grant roles.");

        foreach (var role in roles)
        {
            if (role.TenantId != tenantId)
                throw AuthHttpException.Forbidden("Cannot grant a role outside the target tenant.");
            var acl = await db.Set<RoleAcl>().AsNoTracking()
                .FirstOrDefaultAsync(a => a.RoleId == role.Id && a.TenantId == tenantId && a.DeletedAt == null);
            if (acl is null) continue;
            AssertGrantSnapshot(actorAcl,
                acl.IsSuperAdmin,
                NormalizeGrantFeatureList(JsonArray.Parse(acl.FeaturesJson)),
                JsonArray.Parse(acl.OrganizationsJson) is { } orgs ? NormalizeGrantFeatureList(orgs) : null,
                organizationsProvided: true);
        }
        return roles;
    }

    private static async Task<List<Role>> ResolveRolesForGrant(AppDbContext db, string[] tokens, Guid? tenantId)
    {
        var roles = new List<Role>();
        var missing = new List<string>();
        foreach (var token in tokens)
        {
            Role? role;
            if (Guid.TryParse(token, out var roleId))
            {
                role = await db.Set<Role>().AsNoTracking().FirstOrDefaultAsync(r =>
                    r.Id == roleId && r.DeletedAt == null && (tenantId == null || r.TenantId == tenantId));
            }
            else
            {
                role = await db.Set<Role>().AsNoTracking().FirstOrDefaultAsync(r =>
                    r.Name == token && r.DeletedAt == null && (tenantId == null || r.TenantId == tenantId));
            }
            if (role is null) missing.Add(token); else roles.Add(role);
        }
        if (missing.Count > 0)
        {
            var labels = string.Join(", ", missing.Select(m => $"\"{m}\""));
            throw new AuthHttpException(400, new { error = $"Role(s) not found: {labels}" });
        }
        return roles;
    }

    private static void AssertGrantSnapshot(
        ActorAcl actorAcl, bool requestedIsSuperAdmin, string[] requestedFeatures,
        string[]? requestedOrganizations, bool organizationsProvided)
    {
        if (requestedIsSuperAdmin)
            throw AuthHttpException.Forbidden("Only super administrators can grant super admin access.");

        var grantable = actorAcl.Features.Where(g => g != "*").ToArray();
        foreach (var feature in requestedFeatures)
        {
            if (feature == "*")
                throw AuthHttpException.Forbidden("Only super administrators can grant global wildcard access.");
            if (feature.EndsWith(".*", StringComparison.Ordinal))
            {
                if (!FeatureMatch.HasFeature(grantable, feature))
                    throw AuthHttpException.Forbidden($"Cannot grant feature wildcard {feature}.");
                continue;
            }
            if (!FeatureMatch.HasFeature(grantable, feature))
                throw AuthHttpException.Forbidden($"Cannot grant feature {feature}.");
        }

        if (organizationsProvided)
            AssertGrantOrganizations(actorAcl.Organizations, requestedOrganizations);
    }

    private static void AssertGrantOrganizations(string[]? actorOrgs, string[]? requestedOrgs)
    {
        if (actorOrgs is null || actorOrgs.Contains("__all__")) return;
        if (requestedOrgs is null || requestedOrgs.Contains("__all__"))
            throw AuthHttpException.Forbidden("Cannot grant unrestricted organization access.");
        foreach (var org in requestedOrgs)
            if (!actorOrgs.Contains(org))
                throw AuthHttpException.Forbidden("Cannot grant organization access outside actor scope.");
    }
}
