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
/// /api/auth/roles — 1:1 port of upstream api/roles/route.ts. Hand-written GET (with the
/// test-pinned empty-envelope quirk) plus command-backed POST(201)/PUT(200)/DELETE(200).
/// </summary>
public sealed class RolesRouteGroup : IAuthRouteGroup
{
    public sealed record RolesQuery(Guid? Id, int Page, int PageSize, string? Search, Guid? TenantId);

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/roles", async (HttpContext http, AppDbContext db, IRbacService rbac) =>
        {
            var auth = HttpContextAuth.Current(http);
            var query = TryParseQuery(http.Request.Query, out var q) ? q : null;
            var result = await ListAsync(db, rbac, auth, query);
            return Results.Json(result);
        }).RequireFeatures("auth.roles.list");

        routes.MapPost("/api/auth/roles", CreateAsync).RequireFeatures("auth.roles.manage");
        routes.MapPut("/api/auth/roles", UpdateAsync).RequireFeatures("auth.roles.manage");
        routes.MapDelete("/api/auth/roles", DeleteAsync).RequireFeatures("auth.roles.manage");
    }

    // ---- GET (hand-written; empty-envelope quirk) ---------------------------------------------

    /// <summary>
    /// Testable list handler. <paramref name="auth"/> null OR <paramref name="query"/> null (Zod
    /// parse failure) reproduces the upstream quirk: 200 <c>{items:[],total:0,totalPages:1}</c>
    /// (no <c>isSuperAdmin</c> key).
    /// </summary>
    public static async Task<object> ListAsync(
        AppDbContext db, IRbacService rbac, AuthContext? auth, RolesQuery? query)
    {
        if (auth is null || query is null)
            return new { items = Array.Empty<object>(), total = 0, totalPages = 1 };

        bool isSuperAdmin;
        try { isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin; }
        catch { isSuperAdmin = false; }

        var actorTenantId = auth.TenantId;
        if (!isSuperAdmin && actorTenantId is null)
            return new { items = Array.Empty<object>(), total = 0, totalPages = 1, isSuperAdmin };

        HashSet<Guid>? superAdminRoleIds = null;
        if (!isSuperAdmin && actorTenantId is { } at)
        {
            superAdminRoleIds = (await db.Set<RoleAcl>().AsNoTracking()
                .Where(a => a.TenantId == at && a.IsSuperAdmin && a.DeletedAt == null)
                .Select(a => a.RoleId).ToListAsync()).ToHashSet();
        }

        var tenantFilter = isSuperAdmin ? query.TenantId : null;

        var rows = db.Set<Role>().AsNoTracking().Where(r => r.DeletedAt == null);
        if (query.Id is { } id) rows = rows.Where(r => r.Id == id);
        if (!string.IsNullOrEmpty(query.Search))
        {
            // Provider-agnostic case-insensitive LIKE (Auth references EF Relational, not Npgsql):
            // lower(name) LIKE lower(pattern). // PARITY-TODO: exact ILIKE collation semantics.
            var pattern = $"%{AuthRouteHelpers.EscapeLike(query.Search).ToLowerInvariant()}%";
            rows = rows.Where(r => EF.Functions.Like(r.Name.ToLower(), pattern, "\\"));
        }
        if (!isSuperAdmin && actorTenantId is { } tenant)
        {
            rows = rows.Where(r => r.TenantId == tenant && r.Name != "superadmin");
            if (superAdminRoleIds is { Count: > 0 })
                rows = rows.Where(r => !superAdminRoleIds.Contains(r.Id));
        }
        else if (tenantFilter is { } tf)
        {
            rows = rows.Where(r => r.TenantId == tf);
        }

        var count = await rows.CountAsync();
        var page = await rows.OrderBy(r => r.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        var roleIds = page.Select(r => r.Id).ToList();
        var counts = roleIds.Count == 0
            ? new Dictionary<Guid, int>()
            : (await db.Set<UserRole>().AsNoTracking()
                .Where(l => roleIds.Contains(l.RoleId) && l.DeletedAt == null)
                .GroupBy(l => l.RoleId)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToListAsync()).ToDictionary(x => x.RoleId, x => x.Count);

        var items = page.Select(r =>
        {
            var exposeTenant = isSuperAdmin || (auth.TenantId is { } t && r.TenantId == t);
            return (object)new
            {
                id = r.Id.ToString(),
                name = r.Name,
                usersCount = counts.TryGetValue(r.Id, out var c) ? c : 0,
                tenantId = (string?)r.TenantId.ToString(),
                tenantIds = exposeTenant ? new[] { r.TenantId.ToString() } : Array.Empty<string>(),
                tenantName = exposeTenant ? r.TenantId.ToString() : null,
                updatedAt = r.UpdatedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            };
        }).ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)query.PageSize));
        return new { items, total = count, totalPages, isSuperAdmin };
    }

    public static bool TryParseQuery(IQueryCollection q, out RolesQuery query)
    {
        query = default!;
        if (!QueryParse.OptionalGuid(q["id"], out var id)) return false;
        if (!QueryParse.CoerceIntWithDefault(q["page"], 1, 1, int.MaxValue, out var page)) return false;
        if (!QueryParse.CoerceIntWithDefault(q["pageSize"], 50, 1, 100, out var pageSize)) return false;
        if (!QueryParse.OptionalGuid(q["tenantId"], out var tenantId)) return false;
        var search = q["search"].Count > 0 ? q["search"].ToString() : null;
        if (string.IsNullOrEmpty(search)) search = null;
        query = new RolesQuery(id, page, pageSize, search, tenantId);
        return true;
    }

    // ---- POST / PUT / DELETE (command semantics inlined) --------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        try
        {
            // mapInput: preserve an explicit null so the command rejects it; else enforceTenantSelection.
            var tenantExplicitNull = body.IsNullProperty("tenantId");
            Guid? resolvedTenantId = null;
            if (!tenantExplicitNull)
            {
                var provided = body.HasProperty("tenantId");
                Guid? requested = null;
                if (provided)
                {
                    if (!body.TryGetString("tenantId", out var ts) || !Guid.TryParse(ts, out var tg))
                        return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                    requested = tg;
                }
                var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
                resolvedTenantId = TenantAccess.EnforceTenantSelection(isSuperAdmin, auth.TenantId, provided, requested);
            }

            // command execute
            if (tenantExplicitNull)
                return Results.Json(new { error = "tenantId cannot be null — global roles are not supported" }, statusCode: 400);
            if (!body.TryGetString("name", out var name) || name.Length < 2 || name.Length > 100)
                return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            if (IsReservedRoleName(name))
                return Results.Json(new { error = "Role name is reserved" }, statusCode: 400);

            var tenantId = resolvedTenantId ?? auth.TenantId;
            if (tenantId is null)
                return Results.Json(new { error = "tenantId is required — global roles are not supported" }, statusCode: 400);

            var role = new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId.Value, CreatedAt = DateTimeOffset.UtcNow };
            db.Set<Role>().Add(role);
            await db.SaveChangesAsync();

            await AuthRouteHelpers.EmitAsync(http, "auth.role.created", new { id = role.Id.ToString(), tenantId = role.TenantId.ToString() });
            return Results.Json(new { id = role.Id.ToString() }, statusCode: 201);
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        if (!body.TryGetString("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);

        try
        {
            var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
            await GrantChecks.AssertActorCanModifySuperAdminRoleTarget(
                db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
            await GrantChecks.AssertActorCanAccessRoleTarget(
                db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);

            var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null);
            if (role is null) return Results.Json(new { error = "Role not found" }, statusCode: 404);

            var actorTenantScope = isSuperAdmin ? (Guid?)null : auth.TenantId;
            if (actorTenantScope is { } scope && role.TenantId != scope)
                return Results.Json(new { error = "Role not found" }, statusCode: 404);

            string? nameProvided = body.TryGetString("name", out var nm) ? nm : null;
            if (nameProvided is not null)
            {
                if (nameProvided.Length < 2 || nameProvided.Length > 100)
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                if (nameProvided != role.Name)
                {
                    if (IsReservedRoleName(nameProvided))
                        return Results.Json(new { error = "Role name is reserved" }, statusCode: 400);
                    var assignments = await db.Set<UserRole>().CountAsync(l => l.RoleId == id && l.DeletedAt == null);
                    if (assignments > 0)
                        return Results.Json(new { error = "Role name cannot be changed while users are assigned" }, statusCode: 400);
                }
            }

            Guid? tenantProvided = null;
            var tenantIsProvided = body.HasProperty("tenantId") && !body.IsNullProperty("tenantId");
            if (tenantIsProvided)
            {
                if (!body.TryGetString("tenantId", out var ts) || !Guid.TryParse(ts, out var tg))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                tenantProvided = tg;
                if (tg != role.TenantId)
                {
                    var assignments = await db.Set<UserRole>().CountAsync(l => l.RoleId == id && l.DeletedAt == null);
                    if (assignments > 0)
                        return Results.Json(new { error = "Role cannot be moved to another tenant while users are assigned" }, statusCode: 400);
                    var acls = await db.Set<RoleAcl>().Where(a => a.RoleId == id).ToListAsync();
                    db.Set<RoleAcl>().RemoveRange(acls);
                }
            }

            if (nameProvided is not null) role.Name = nameProvided;
            if (tenantProvided is { } tp) role.TenantId = tp;
            role.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            await AuthRouteHelpers.EmitAsync(http, "auth.role.updated", new { id = role.Id.ToString(), tenantId = role.TenantId.ToString() });
            return Results.Json(new { ok = true });
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var idStr = http.Request.Query["id"].ToString();
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Role id required" }, statusCode: 400);

        try
        {
            var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
            await GrantChecks.AssertActorCanModifySuperAdminRoleTarget(
                db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
            await GrantChecks.AssertActorCanAccessRoleTarget(
                db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);

            var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null);
            if (role is null) return Results.Json(new { error = "Role not found" }, statusCode: 404);

            var actorTenantScope = isSuperAdmin ? (Guid?)null : auth.TenantId;
            if (actorTenantScope is { } scope && role.TenantId != scope)
                return Results.Json(new { error = "Role not found" }, statusCode: 404);

            var active = await db.Set<UserRole>().CountAsync(l => l.RoleId == id && l.DeletedAt == null);
            if (active > 0) return Results.Json(new { error = "Role has assigned users" }, statusCode: 400);

            var acls = await db.Set<RoleAcl>().Where(a => a.RoleId == id).ToListAsync();
            db.Set<RoleAcl>().RemoveRange(acls);
            db.Set<Role>().Remove(role);
            await db.SaveChangesAsync();

            await AuthRouteHelpers.EmitAsync(http, "auth.role.deleted", new { id = id.ToString(), tenantId = role.TenantId.ToString() });
            return Results.Json(new { ok = true });
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static bool IsReservedRoleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim().ToLowerInvariant();
        return n is "superadmin" or "admin";
    }
}
