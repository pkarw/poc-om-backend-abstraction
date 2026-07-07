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
/// GET+PUT /api/auth/roles/acl — 1:1 port of upstream api/roles/acl/route.ts. Both require
/// <c>auth.acl.manage</c>. PUT always answers <c>{"ok":true,"sanitized":false}</c>.
/// </summary>
public sealed class RolesAclRouteGroup : IAuthRouteGroup
{
    private const string Iso = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/roles/acl", GetAsync).RequireFeatures("auth.acl.manage");
        routes.MapPut("/api/auth/roles/acl", PutAsync).RequireFeatures("auth.acl.manage");
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        if (!QueryParse.OptionalGuid(http.Request.Query["roleId"], out var roleIdOpt) || roleIdOpt is null)
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);
        if (!QueryParse.OptionalGuid(http.Request.Query["tenantId"], out var queryTenantId))
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);
        var roleId = roleIdOpt.Value;

        try
        {
            var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
            var role = await LoadRole(db, roleId, isSuperAdmin, auth.TenantId);
            if (role is null) return Results.Json(new { error = "Not found" }, statusCode: 404);

            var tenantScope = ResolveTenantScope(queryTenantId, role.TenantId, auth.TenantId, isSuperAdmin, out var forbidden);
            if (forbidden) return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (!isSuperAdmin)
                await GrantChecks.AssertActorCanModifySuperAdminRoleTarget(
                    db, rbac, auth.UserId, tenantScope, auth.OrganizationId, roleId, actorIsSuperAdmin: false);

            RoleAcl? acl = tenantScope is { } ts
                ? await db.Set<RoleAcl>().AsNoTracking().FirstOrDefaultAsync(a => a.RoleId == roleId && a.TenantId == ts && a.DeletedAt == null)
                : null;

            var response = acl is not null
                ? new
                {
                    isSuperAdmin = acl.IsSuperAdmin,
                    features = JsonArray.Parse(acl.FeaturesJson) ?? Array.Empty<string>(),
                    organizations = JsonArray.Parse(acl.OrganizationsJson),
                    updatedAt = acl.UpdatedAt?.UtcDateTime.ToString(Iso),
                }
                : new { isSuperAdmin = false, features = Array.Empty<string>(), organizations = (string[]?)null, updatedAt = (string?)null };

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
        if (!body.TryGetString("roleId", out var roleIdStr) || !Guid.TryParse(roleIdStr, out var roleId))
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);
        if (!QueryParse.OptionalGuid(body.TryGetString("tenantId", out var tId) ? tId : string.Empty, out var bodyTenantId))
            return Results.Json(new { error = "Invalid input" }, statusCode: 400);

        try
        {
            var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
            var role = await LoadRole(db, roleId, isSuperAdmin, auth.TenantId);
            if (role is null) return Results.Json(new { error = "Not found" }, statusCode: 404);

            var targetTenantId = ResolveTenantScope(bodyTenantId, role.TenantId, auth.TenantId, isSuperAdmin, out var forbidden);
            if (forbidden) return Results.Json(new { error = "Forbidden" }, statusCode: 403);
            if (!isSuperAdmin && targetTenantId is null) targetTenantId = auth.TenantId;
            if (targetTenantId is null) return Results.Json(new { error = "Tenant required" }, statusCode: 400);
            if (!isSuperAdmin && targetTenantId != auth.TenantId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (!isSuperAdmin)
                await GrantChecks.AssertActorCanModifySuperAdminRoleTarget(
                    db, rbac, auth.UserId, targetTenantId, auth.OrganizationId, roleId, actorIsSuperAdmin: false);

            var acl = await db.Set<RoleAcl>().FirstOrDefaultAsync(a => a.RoleId == roleId && a.TenantId == targetTenantId && a.DeletedAt == null);
            var isNew = acl is null;
            // Optimistic lock is a no-op when the x-om-ext-optimistic-lock header is absent (spec 02 R40). // PARITY-TODO: header enforcement.
            acl ??= new RoleAcl { Id = Guid.NewGuid(), RoleId = roleId, TenantId = targetTenantId.Value, CreatedAt = DateTimeOffset.UtcNow, IsSuperAdmin = false };

            var existingIsSuperAdmin = acl.IsSuperAdmin;
            var existingFeatures = GrantChecks.NormalizeGrantFeatureList(JsonArray.Parse(acl.FeaturesJson));
            var existingOrganizations = NormalizeOrganizations(JsonArray.Parse(acl.OrganizationsJson));

            var requestedIsSuperAdmin = body.TryGetBool("isSuperAdmin") ?? existingIsSuperAdmin;
            var requestedFeatures = body.HasProperty("features")
                ? GrantChecks.NormalizeGrantFeatureList(body.TryGetStringArray("features"))
                : existingFeatures;
            var requestedOrganizations = body.HasProperty("organizations")
                ? NormalizeOrganizations(body.TryGetStringArray("organizations"))
                : existingOrganizations;

            await GrantChecks.AssertActorCanGrantAcl(
                db, rbac, auth.UserId, targetTenantId, auth.OrganizationId,
                requestedIsSuperAdmin, requestedFeatures, requestedOrganizations);

            acl.OrganizationsJson = JsonArray.Serialize(requestedOrganizations);
            acl.IsSuperAdmin = requestedIsSuperAdmin;
            acl.FeaturesJson = JsonArray.Serialize(requestedFeatures);
            if (!isNew) acl.UpdatedAt = DateTimeOffset.UtcNow;
            if (isNew) db.Set<RoleAcl>().Add(acl);
            await db.SaveChangesAsync();

            await rbac.InvalidateTenantCache(targetTenantId.Value); // no-op in port

            return Results.Json(new { ok = true, sanitized = false });
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static async Task<Role?> LoadRole(AppDbContext db, Guid roleId, bool isSuperAdmin, Guid? authTenantId)
    {
        var q = db.Set<Role>().AsNoTracking().Where(r => r.Id == roleId);
        if (!isSuperAdmin && authTenantId is { } at)
            q = q.Where(r => r.TenantId == at); // roles.tenant_id is NOT NULL; the `OR tenantId null` branch is unreachable.
        return await q.FirstOrDefaultAsync();
    }

    private static Guid? ResolveTenantScope(Guid? requested, Guid roleTenantId, Guid? authTenantId, bool isSuperAdmin, out bool forbidden)
    {
        forbidden = false;
        var tenantScope = requested ?? roleTenantId;
        if (requested is { } r && r != tenantScope)
        {
            if (isSuperAdmin || r == authTenantId) tenantScope = r;
            else { forbidden = true; return null; }
        }
        return tenantScope;
    }

    private static string[]? NormalizeOrganizations(string[]? organizations) =>
        organizations is null ? null : GrantChecks.NormalizeGrantFeatureList(organizations);
}
