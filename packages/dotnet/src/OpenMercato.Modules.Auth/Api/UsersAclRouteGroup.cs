using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET+PUT /api/auth/users/acl — 1:1 port of upstream api/users/acl/route.ts. Non-super-admin PUTs
/// are sanitized (tenant-restricted features stripped; <c>requestedIsSuperAdmin</c> defaults false);
/// an empty effective ACL deletes the row.
/// </summary>
public sealed class UsersAclRouteGroup : IAuthRouteGroup
{
    private const string Iso = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/users/acl", GetAsync).RequireFeatures("auth.acl.manage");
        routes.MapPut("/api/auth/users/acl", PutAsync).RequireFeatures("auth.acl.manage");
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        if (!QueryParse.OptionalGuid(http.Request.Query["userId"], out var userIdOpt) || userIdOpt is null)
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);
        var userId = userIdOpt.Value;
        try
        {
            var actorIsSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin;
            if (!actorIsSuperAdmin)
            {
                await GrantChecks.AssertActorCanModifySuperAdminUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, userId, actorIsSuperAdmin: false);
                await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, userId, actorIsSuperAdmin: false);
            }

            var acl = await db.Set<UserAcl>().AsNoTracking()
                .FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == auth.TenantId && a.DeletedAt == null);
            var response = acl is not null
                ? new
                {
                    hasCustomAcl = true,
                    isSuperAdmin = acl.IsSuperAdmin,
                    features = JsonArray.Parse(acl.FeaturesJson) ?? Array.Empty<string>(),
                    organizations = JsonArray.Parse(acl.OrganizationsJson),
                    updatedAt = acl.UpdatedAt?.UtcDateTime.ToString(Iso),
                }
                : new { hasCustomAcl = false, isSuperAdmin = false, features = Array.Empty<string>(), organizations = (string[]?)null, updatedAt = (string?)null };
            return Results.Json(response);
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static async Task<IResult> PutAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        if (!body.TryGetString("userId", out var userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);
        try
        {
            var actorIsSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin;
            if (!actorIsSuperAdmin)
            {
                await GrantChecks.AssertActorCanModifySuperAdminUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, userId, actorIsSuperAdmin: false);
                await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, userId, actorIsSuperAdmin: false);
            }

            var requestedFeatures = GrantChecks.NormalizeGrantFeatureList(body.TryGetStringArray("features"));
            var organizations = body.TryGetStringArray("organizations") is { } o ? GrantChecks.NormalizeGrantFeatureList(o) : null;

            var acl = await db.Set<UserAcl>().FirstOrDefaultAsync(a => a.UserId == userId && a.TenantId == auth.TenantId && a.DeletedAt == null);
            // Optimistic lock: no-op without the expected-version header (spec 02 R40). // PARITY-TODO
            var existingIsSuperAdmin = acl?.IsSuperAdmin ?? false;
            var existingFeatures = acl is not null ? GrantChecks.NormalizeGrantFeatureList(JsonArray.Parse(acl.FeaturesJson)) : Array.Empty<string>();
            var requestedIsSuperAdmin = body.TryGetBool("isSuperAdmin") ?? false;

            await GrantChecks.AssertActorCanGrantAcl(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId,
                requestedIsSuperAdmin, requestedFeatures, organizations);

            var effectiveFeatures = actorIsSuperAdmin ? requestedFeatures : SanitizeTenantFeatures(requestedFeatures);
            var effectiveIsSuperAdmin = requestedIsSuperAdmin;
            if (!actorIsSuperAdmin)
            {
                if (requestedIsSuperAdmin && !existingIsSuperAdmin)
                    throw AuthHttpException.Forbidden("Only super administrators can grant super admin access.");
                effectiveIsSuperAdmin = existingIsSuperAdmin && requestedIsSuperAdmin == false ? false : existingIsSuperAdmin;
            }

            var hasCustomAcl = effectiveIsSuperAdmin || effectiveFeatures.Length > 0;

            if (!hasCustomAcl)
            {
                if (acl is not null)
                {
                    db.Set<UserAcl>().Remove(acl);
                    await db.SaveChangesAsync();
                }
            }
            else
            {
                var isNew = acl is null;
                acl ??= new UserAcl { Id = Guid.NewGuid(), UserId = userId, TenantId = auth.TenantId ?? Guid.Empty, CreatedAt = DateTimeOffset.UtcNow };
                acl.IsSuperAdmin = effectiveIsSuperAdmin;
                acl.FeaturesJson = JsonArray.Serialize(effectiveFeatures);
                acl.OrganizationsJson = JsonArray.Serialize(organizations);
                if (!isNew) acl.UpdatedAt = DateTimeOffset.UtcNow;
                if (isNew) db.Set<UserAcl>().Add(acl);
                await db.SaveChangesAsync();
            }

            await rbac.InvalidateUserCache(userId); // no-op in port

            var sanitized = !actorIsSuperAdmin &&
                (HasRestrictedChanges(requestedFeatures, effectiveFeatures, existingFeatures) || requestedIsSuperAdmin != effectiveIsSuperAdmin);
            return Results.Json(new { ok = true, sanitized });
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static string[] SanitizeTenantFeatures(string[] features) =>
        features.Where(f => !IsTenantRestricted(f)).ToArray();

    private static bool IsTenantRestricted(string feature) =>
        feature is "*" or "directory.*" || feature.StartsWith("directory.tenants", StringComparison.Ordinal);

    private static bool HasRestrictedChanges(string[] requested, string[] effective, string[] existing)
    {
        if (requested.Length == effective.Length) return false;
        var effectiveSet = effective.ToHashSet();
        var existingSet = existing.ToHashSet();
        if (effectiveSet.Count == existingSet.Count && effectiveSet.All(existingSet.Contains)) return false;
        return true;
    }
}
