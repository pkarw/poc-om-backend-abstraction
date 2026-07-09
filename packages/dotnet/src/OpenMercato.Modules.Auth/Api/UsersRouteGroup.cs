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
/// /api/auth/users — 1:1 port of upstream api/users/route.ts. Hand-written GET (empty-envelope
/// quirk) plus command-backed POST(201)/PUT(200)/DELETE(200) with per-tenant duplicate-email 400.
///
/// Directory is not ported, so tenant/organization <b>names</b> fall back to their ids and the
/// super-admin <c>om_selected_tenant</c> cookie scoping is skipped (// PARITY-TODO); email full-text
/// search (upstream search_tokens) is likewise unavailable, so <c>search</c> matches role names only.
/// </summary>
public sealed class UsersRouteGroup : IAuthRouteGroup
{
    private const string Iso = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public sealed record UsersQuery(
        Guid? Id, int Page, int PageSize, string? Search, string? Name, Guid? OrganizationId, Guid[]? RoleIds);

    private sealed class LinkRow
    {
        public Guid UserId { get; init; }
        public Guid RoleId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/users", async (HttpContext http, AppDbContext db, IRbacService rbac, TenantDataEncryptionService tenc) =>
        {
            var auth = HttpContextAuth.Current(http);
            var query = TryParseQuery(http.Request.Query, out var q) ? q : null;
            var result = await ListAsync(db, rbac, tenc, auth, query);
            return Results.Json(result);
        }).RequireFeatures("auth.users.list");

        routes.MapPost("/api/auth/users", CreateAsync).RequireFeatures("auth.users.create");
        routes.MapPut("/api/auth/users", UpdateAsync).RequireFeatures("auth.users.edit");
        routes.MapDelete("/api/auth/users", DeleteAsync).RequireFeatures("auth.users.delete");
    }

    // ---- GET (hand-written; empty-envelope quirk) ---------------------------------------------

    public static async Task<object> ListAsync(
        AppDbContext db, IRbacService rbac, TenantDataEncryptionService tenc, AuthContext? auth, UsersQuery? query)
    {
        if (auth is null || query is null)
            return new { items = Array.Empty<object>(), total = 0, totalPages = 1 };

        bool isSuperAdmin;
        try { isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin; }
        catch { isSuperAdmin = false; }

        var rows = db.Set<User>().AsNoTracking().Where(u => u.DeletedAt == null);
        var actorTenantId = auth.TenantId;
        Guid? effectiveTenantId = null;

        if (!isSuperAdmin)
        {
            if (actorTenantId is null)
                return new { items = Array.Empty<object>(), total = 0, totalPages = 1, isSuperAdmin };
            effectiveTenantId = actorTenantId;
            var superAdminUserIds = await GrantChecks.ListSuperAdminUserIds(db, actorTenantId);
            if (superAdminUserIds.Count > 0)
                rows = rows.Where(u => !superAdminUserIds.Contains(u.Id));
        }
        // Super-admin: directory-scoped tenant selection is skipped (see class remark).

        if (effectiveTenantId is { } et) rows = rows.Where(u => u.TenantId == et);
        if (query.OrganizationId is { } orgId) rows = rows.Where(u => u.OrganizationId == orgId);

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var pattern = $"%{AuthRouteHelpers.EscapeLike(query.Name.Trim()).ToLowerInvariant()}%";
            rows = rows.Where(u => u.Name != null && EF.Functions.Like(u.Name.ToLower(), pattern, "\\"));
        }

        HashSet<Guid>? idFilter = query.Id is { } qid ? new HashSet<Guid> { qid } : null;
        if (query.RoleIds is { Length: > 0 } roleIds)
        {
            var roleUserIds = (await db.Set<UserRole>().AsNoTracking()
                .Where(l => roleIds.Contains(l.RoleId))
                .Select(l => l.UserId).Distinct().ToListAsync()).ToHashSet();
            if (roleUserIds.Count == 0)
                return new { items = Array.Empty<object>(), total = 0, totalPages = 1 };
            if (idFilter is not null) idFilter.IntersectWith(roleUserIds);
            else idFilter = roleUserIds;
            if (idFilter.Count == 0)
                return new { items = Array.Empty<object>(), total = 0, totalPages = 1 };
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Email/org search require unported infra; match role names only (// PARITY-TODO).
            var pattern = $"%{AuthRouteHelpers.EscapeLike(query.Search.Trim()).ToLowerInvariant()}%";
            var matchedRoleIds = await db.Set<Role>().AsNoTracking()
                .Where(r => r.DeletedAt == null && EF.Functions.Like(r.Name.ToLower(), pattern, "\\"))
                .Select(r => r.Id).ToListAsync();
            var searchUserIds = matchedRoleIds.Count == 0
                ? new List<Guid>()
                : await db.Set<UserRole>().AsNoTracking()
                    .Where(l => matchedRoleIds.Contains(l.RoleId))
                    .Select(l => l.UserId).Distinct().ToListAsync();
            if (searchUserIds.Count == 0)
                return new { items = Array.Empty<object>(), total = 0, totalPages = 1, isSuperAdmin };
            rows = rows.Where(u => searchUserIds.Contains(u.Id));
        }

        if (idFilter is not null)
        {
            var ids = idFilter.ToArray();
            rows = rows.Where(u => ids.Contains(u.Id));
        }

        var count = await rows.CountAsync();
        var page = await rows.OrderBy(u => u.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        // Decrypt each row's email/name with its own tenant DEK (no-op when plaintext / no map).
        foreach (var u in page) tenc.DecryptUserInPlace(db, u);

        var userIds = page.Select(u => u.Id).ToList();
        var links = userIds.Count == 0
            ? new List<LinkRow>()
            : await (from l in db.Set<UserRole>().AsNoTracking()
                     join r in db.Set<Role>().AsNoTracking() on l.RoleId equals r.Id
                     where userIds.Contains(l.UserId)
                     select new LinkRow { UserId = l.UserId, RoleId = r.Id, Name = r.Name })
                    .ToListAsync();

        var roleNames = new Dictionary<Guid, List<string>>();
        var roleIdMap = new Dictionary<Guid, List<string>>();
        foreach (var link in links)
        {
            if (!roleNames.TryGetValue(link.UserId, out var nl)) roleNames[link.UserId] = nl = new List<string>();
            if (!roleIdMap.TryGetValue(link.UserId, out var il)) roleIdMap[link.UserId] = il = new List<string>();
            nl.Add(link.Name);
            il.Add(link.RoleId.ToString());
        }

        var items = page.Select(u =>
        {
            // Preserve upstream key order; hasPassword is present ONLY for ?id= reads.
            var item = new Dictionary<string, object?>
            {
                ["id"] = u.Id.ToString(),
                ["email"] = u.Email,
                ["name"] = u.Name,
                ["organizationId"] = u.OrganizationId?.ToString(),
                ["organizationName"] = u.OrganizationId?.ToString(),
                ["tenantId"] = u.TenantId?.ToString(),
                ["tenantName"] = u.TenantId?.ToString(),
                ["roles"] = roleNames.TryGetValue(u.Id, out var rn) ? rn : new List<string>(),
                ["roleIds"] = roleIdMap.TryGetValue(u.Id, out var ri) ? ri : new List<string>(),
            };
            if (query.Id is not null) item["hasPassword"] = !string.IsNullOrEmpty(u.PasswordHash);
            item["updatedAt"] = u.UpdatedAt?.UtcDateTime.ToString(Iso);
            return (object)item;
        }).ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)query.PageSize));
        return new { items, total = count, totalPages, isSuperAdmin };
    }

    public static bool TryParseQuery(IQueryCollection q, out UsersQuery query)
    {
        query = default!;
        if (!QueryParse.OptionalGuid(q["id"], out var id)) return false;
        if (!QueryParse.CoerceIntWithDefault(q["page"], 1, 1, int.MaxValue, out var page)) return false;
        if (!QueryParse.CoerceIntWithDefault(q["pageSize"], 50, 1, 100, out var pageSize)) return false;
        if (!QueryParse.OptionalGuid(q["organizationId"], out var orgId)) return false;

        var search = q["search"].Count > 0 ? q["search"].ToString() : null;
        if (string.IsNullOrEmpty(search)) search = null;
        var name = q["name"].Count > 0 ? q["name"].ToString() : null;
        if (string.IsNullOrEmpty(name)) name = null;

        Guid[]? roleIds = null;
        var rawRoleIds = q["roleId"].Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray();
        if (rawRoleIds.Length > 0)
        {
            var parsed = new List<Guid>();
            foreach (var raw in rawRoleIds)
            {
                if (!Guid.TryParse(raw, out var g)) return false;
                parsed.Add(g);
            }
            roleIds = parsed.ToArray();
        }

        query = new UsersQuery(id, page, pageSize, search, name, orgId, roleIds);
        return true;
    }

    // ---- POST / PUT / DELETE ------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        HttpContext http, AppDbContext db, IRbacService rbac, EncryptionService enc,
        PasswordHasher hasher, TokenHasher tokens)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        try
        {
            if (!body.TryGetString("email", out var email) || !IsEmail(email))
                return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            if (!body.TryGetString("organizationId", out var orgStr) || !Guid.TryParse(orgStr, out var organizationId))
                return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            var password = body.TryGetString("password", out var pw) ? pw : null;
            var sendInvite = body.TryGetBool("sendInviteEmail") ?? false;
            if (string.IsNullOrEmpty(password) && !sendInvite)
                return Results.Json(new { error = "Either password or sendInviteEmail is required" }, statusCode: 400);
            if (!TryReadName(body, out var name, out var nameError))
                return Results.Json(new { error = nameError }, statusCode: 400);
            var roles = body.TryGetStringArray("roles");

            // Guard: role assignment (tenant resolved from actor — directory not ported).
            await GrantChecks.AssertActorCanGrantRoleTokens(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, roles);

            var tenantId = auth.TenantId; // // PARITY-TODO: upstream derives tenant from organization.tenant.
            var lookup = enc.EmailHashLookupValues(email);
            var duplicate = await db.Set<User>().AsNoTracking().AnyAsync(u =>
                u.EmailHash != null && lookup.Contains(u.EmailHash) && u.TenantId == tenantId && u.DeletedAt == null);
            if (duplicate) return DuplicateEmail();

            var user = new User
            {
                Id = Guid.NewGuid(),
                // Write PLAINTEXT — the SaveChanges interceptor encrypts email/name with the tenant DEK.
                Email = email,
                EmailHash = enc.ComputeEmailHash(email),
                Name = name,
                PasswordHash = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
                IsConfirmed = true,
                OrganizationId = organizationId,
                TenantId = tenantId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Set<User>().Add(user);
            await db.SaveChangesAsync();

            if (roles is { Length: > 0 })
                await SyncUserRoles(db, user.Id, roles, tenantId);

            if (sendInvite)
            {
                var raw = tokens.Generate();
                db.Set<PasswordReset>().Add(new PasswordReset
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Token = tokens.Hash(raw),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(48),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
                // Email delivery is out of scope in the port (no mailer). // PARITY-TODO
            }

            await AuthRouteHelpers.EmitAsync(http, "auth.user.created",
                new { id = user.Id.ToString(), organizationId = user.OrganizationId?.ToString(), tenantId = user.TenantId?.ToString() });

            return Results.Json(new { id = user.Id.ToString() }, statusCode: 201);
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext http, AppDbContext db, IRbacService rbac, EncryptionService enc, PasswordHasher hasher)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        if (!body.TryGetString("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
        try
        {
            var isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin;
            await GrantChecks.AssertActorCanModifySuperAdminUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
            await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
            var roles = body.TryGetStringArray("roles");
            await GrantChecks.AssertActorCanGrantRoleTokens(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, roles);

            var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null);
            if (user is null) return Results.Json(new { error = "User not found" }, statusCode: 404);
            var actorTenantScope = isSuperAdmin ? (Guid?)null : auth.TenantId;
            if (actorTenantScope is { } scope && user.TenantId != scope)
                return Results.Json(new { error = "User not found" }, statusCode: 404);

            string? email = body.TryGetString("email", out var em) ? em : null;
            if (email is not null)
            {
                if (!IsEmail(email)) return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                var lookup = enc.EmailHashLookupValues(email);
                var dup = await db.Set<User>().AsNoTracking().AnyAsync(u =>
                    u.EmailHash != null && lookup.Contains(u.EmailHash) && u.TenantId == user.TenantId && u.Id != id && u.DeletedAt == null);
                if (dup) return DuplicateEmail();
                // Write PLAINTEXT — the SaveChanges interceptor re-encrypts with the tenant DEK on save.
                user.Email = email;
                user.EmailHash = enc.ComputeEmailHash(email);
            }

            if (body.HasProperty("name"))
            {
                if (!TryReadName(body, out var name, out var nameError))
                    return Results.Json(new { error = nameError }, statusCode: 400);
                user.Name = name;
            }
            if (body.TryGetString("organizationId", out var orgStr))
            {
                if (!Guid.TryParse(orgStr, out var organizationId))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                user.OrganizationId = organizationId; // tenant unchanged — directory not ported. // PARITY-TODO
            }

            var passwordChanged = false;
            if (body.TryGetString("password", out var pw) && !string.IsNullOrEmpty(pw))
            {
                user.PasswordHash = hasher.Hash(pw);
                passwordChanged = true;
            }
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            if (passwordChanged)
            {
                var sessions = await db.Set<Session>().Where(s => s.UserId == id).ToListAsync();
                db.Set<Session>().RemoveRange(sessions);
                await db.SaveChangesAsync();
            }

            if (roles is not null)
                await SyncUserRoles(db, id, roles, user.TenantId);

            await AuthRouteHelpers.EmitAsync(http, "auth.user.updated",
                new { id = user.Id.ToString(), organizationId = user.OrganizationId?.ToString(), tenantId = user.TenantId?.ToString() });

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
            return Results.Json(new { error = "User id required" }, statusCode: 400);
        try
        {
            var isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin;
            await GrantChecks.AssertActorCanModifySuperAdminUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
            await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);

            var actorTenantScope = isSuperAdmin ? (Guid?)null : auth.TenantId;
            var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null
                && (actorTenantScope == null || u.TenantId == actorTenantScope));
            if (user is null) return Results.Json(new { error = "User not found" }, statusCode: 404);

            db.Set<UserAcl>().RemoveRange(await db.Set<UserAcl>().Where(a => a.UserId == id).ToListAsync());
            db.Set<UserRole>().RemoveRange(await db.Set<UserRole>().Where(a => a.UserId == id).ToListAsync());
            db.Set<Session>().RemoveRange(await db.Set<Session>().Where(a => a.UserId == id).ToListAsync());
            db.Set<PasswordReset>().RemoveRange(await db.Set<PasswordReset>().Where(a => a.UserId == id).ToListAsync());
            db.Set<User>().Remove(user);
            await db.SaveChangesAsync();

            await AuthRouteHelpers.EmitAsync(http, "auth.user.deleted",
                new { id = id.ToString(), organizationId = user.OrganizationId?.ToString(), tenantId = user.TenantId?.ToString() });

            return Results.Json(new { ok = true });
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static IResult DuplicateEmail() =>
        Results.Json(new
        {
            error = "Email already in use",
            fieldErrors = new { email = "Email already in use" },
            details = new[] { new { path = new[] { "email" }, message = "Email already in use", code = "duplicate", origin = "validation" } },
        }, statusCode: 400);

    private static bool IsEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@') && value.IndexOf('@') > 0 && value.IndexOf('@') < value.Length - 1;

    private static bool TryReadName(System.Text.Json.JsonElement body, out string? name, out string error)
    {
        name = null; error = string.Empty;
        if (!body.HasProperty("name")) return true;
        if (body.IsNullProperty("name")) return true;
        if (!body.TryGetString("name", out var raw)) { error = "Invalid payload"; return false; }
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) { name = null; return true; }
        if (trimmed.Length > 120) { error = "Invalid payload"; return false; }
        name = trimmed;
        return true;
    }

    /// <summary>Reconcile a user's role memberships to <paramref name="desired"/> (upstream syncUserRoles).</summary>
    private static async Task SyncUserRoles(AppDbContext db, Guid userId, string[] desired, Guid? tenantId)
    {
        var unique = desired.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToArray();
        var resolved = new List<Role>();
        var missing = new List<string>();
        foreach (var token in unique)
        {
            Role? role;
            if (Guid.TryParse(token, out var rid))
                role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == rid && (tenantId == null || r.TenantId == tenantId));
            else
                role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == token && (tenantId == null || r.TenantId == tenantId));
            if (role is null) missing.Add(token); else resolved.Add(role);
        }
        if (missing.Count > 0)
        {
            var labels = string.Join(", ", missing.Select(m => $"\"{m}\""));
            throw new AuthHttpException(400, new { error = $"Role(s) not found: {labels}" });
        }

        var desiredIds = resolved.Select(r => r.Id).ToHashSet();
        var currentLinks = await db.Set<UserRole>().Where(l => l.UserId == userId).ToListAsync();
        var currentIds = currentLinks.Select(l => l.RoleId).ToHashSet();
        foreach (var link in currentLinks.Where(l => !desiredIds.Contains(l.RoleId)))
            db.Set<UserRole>().Remove(link);
        foreach (var role in resolved.Where(r => !currentIds.Contains(r.Id)))
            db.Set<UserRole>().Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }
}
