using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Port of <c>api/sidebar/preferences/route.ts</c>: GET/PUT/DELETE <c>/api/auth/sidebar/preferences</c>.
/// GET requires auth (role-scoped read gates <c>auth.sidebar.manage</c> in-handler); PUT/DELETE gate the
/// feature at the dispatcher. Settings shape, role application, optimistic-lock 409s and the exact
/// 200/400/401/403/404/409 bodies mirror upstream. Cache-tag invalidation is a documented no-op.
/// </summary>
public sealed class SidebarPreferencesRoutes : IAuthRouteGroup
{
    private const string FeatureManage = "auth.sidebar.manage";
    private static readonly string[] ManageFeatures = { FeatureManage };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/sidebar/preferences", GetAsync).RequireAuth();
        routes.MapPut("/api/auth/sidebar/preferences", PutAsync).RequireFeatures(FeatureManage);
        routes.MapDelete("/api/auth/sidebar/preferences", DeleteAsync).RequireFeatures(FeatureManage);
    }

    // NOTE: these handlers MUST take at least one DI parameter besides HttpContext. A minimal-API
    // handler whose only parameter is HttpContext is bound as a RequestDelegate, and its returned
    // IResult is then IGNORED (the response comes back 200 with no body and no content-type). That
    // silently blanked every sidebar response. (OM integration test TC-AUTH-023.)
    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var svc = new SidebarPreferencesService(db);
        var locale = SidebarLocale.Resolve(http);

        var roleIdParam = http.Request.Query["roleId"].ToString();
        var canApplyToRoles = await rbac.UserHasAllFeatures(auth.UserId, ManageFeatures, auth.TenantId, auth.OrganizationId);

        // Role-scoped read: requires auth.sidebar.manage.
        if (!string.IsNullOrEmpty(roleIdParam))
        {
            if (!canApplyToRoles)
                return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Forbidden", ["requiredFeatures"] = ManageFeatures }, 403);

            var role = Guid.TryParse(roleIdParam, out var rid)
                ? await svc.FindRoleInScopeAsync(rid, auth.TenantId)
                : null;
            if (role is null)
                return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Role not found" }, 404);

            var rolePrefs = await svc.LoadRoleSidebarPreferencesAsync(new[] { role.Id }, auth.TenantId);
            var pref = rolePrefs.TryGetValue(role.Id, out var p) ? p : null;
            var rolesPayload = await svc.LoadRolesPayloadAsync(auth.TenantId);
            var roleVersion = await svc.LoadRoleSidebarPreferenceUpdatedAtAsync(role.Id, auth.TenantId);

            return SidebarHttp.Json(new Dictionary<string, object?>
            {
                ["locale"] = locale,
                ["settings"] = (pref ?? SidebarSettings.Default()).ToDict(),
                ["canApplyToRoles"] = canApplyToRoles,
                ["roles"] = rolesPayload.Select(SidebarHttp.RoleEntry).ToList(),
                ["scope"] = new Dictionary<string, object?> { ["type"] = "role", ["roleId"] = role.Id },
                ["updatedAt"] = roleVersion.HasValue ? SidebarJson.ToIsoOrNull(roleVersion.Value.UpdatedAt) : null,
            });
        }

        var settings = await svc.LoadSidebarPreferenceAsync(auth.UserId, auth.TenantId, auth.OrganizationId);
        var roles = canApplyToRoles ? await svc.LoadRolesPayloadAsync(auth.TenantId) : new List<SidebarRoleEntry>();
        var userVersion = await svc.LoadSidebarPreferenceUpdatedAtAsync(auth.UserId, auth.TenantId, auth.OrganizationId);

        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["settings"] = settings.ToDict(),
            ["canApplyToRoles"] = canApplyToRoles,
            ["roles"] = roles.Select(SidebarHttp.RoleEntry).ToList(),
            ["scope"] = new Dictionary<string, object?> { ["type"] = "user" },
            ["updatedAt"] = userVersion.HasValue ? SidebarJson.ToIsoOrNull(userVersion.Value.UpdatedAt) : null,
        });
    }

    private static async Task<IResult> PutAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var svc = new SidebarPreferencesService(db);

        var body = await SidebarHttp.ReadJsonAsync(http);
        if (body is null)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid JSON" }, 400);

        var parsed = SidebarValidation.ValidatePreferences(body.Value);
        if (!parsed.Ok)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid payload", ["details"] = parsed.Details }, 400);

        var payload = BuildUserPayload(parsed.Value!.Settings);
        var locale = SidebarLocale.Resolve(http);
        var canApplyToRoles = await rbac.UserHasAllFeatures(auth.UserId, ManageFeatures, auth.TenantId, auth.OrganizationId);
        var scopeType = parsed.Value.Scope?.Type ?? "user";

        // Role-scoped write.
        if (scopeType == "role")
        {
            if (!canApplyToRoles)
                return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Forbidden", ["requiredFeatures"] = ManageFeatures }, 403);

            var roleId = parsed.Value.Scope!.RoleId!.Value;
            var role = await svc.FindRoleInScopeAsync(roleId, auth.TenantId);
            if (role is null)
                return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Role not found" }, 404);

            var existing = await svc.LoadRoleSidebarPreferenceUpdatedAtAsync(role.Id, auth.TenantId);
            if (existing.HasValue)
            {
                var conflict = SidebarOptimisticLock.Check("auth.role_sidebar_preference", existing.Value.UpdatedAt, http.Request);
                if (conflict is not null) return SidebarHttp.Json(conflict, 409);
            }

            var saved = await svc.SaveRoleSidebarPreferenceAsync(role.Id, auth.TenantId, locale, payload);
            var savedVersion = await svc.LoadRoleSidebarPreferenceUpdatedAtAsync(role.Id, auth.TenantId);
            InvalidateCacheTags(); // PARITY-TODO: cache.deleteByTags(['nav:sidebar:role:<id>']) is a no-op here.
            var rolesPayload = await svc.LoadRolesPayloadAsync(auth.TenantId);

            return SidebarHttp.Json(new Dictionary<string, object?>
            {
                ["locale"] = locale,
                ["settings"] = saved.ToDict(),
                ["canApplyToRoles"] = canApplyToRoles,
                ["roles"] = rolesPayload.Select(SidebarHttp.RoleEntry).ToList(),
                ["scope"] = new Dictionary<string, object?> { ["type"] = "role", ["roleId"] = role.Id },
                ["updatedAt"] = savedVersion.HasValue ? SidebarJson.ToIsoOrNull(savedVersion.Value.UpdatedAt) : null,
                ["appliedRoles"] = new List<object?>(),
                ["clearedRoles"] = new List<object?>(),
            });
        }

        // User-scoped write (may fan out to roles).
        var applyToRoles = (parsed.Value.ApplyToRoles ?? new List<string>())
            .Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var clearRoleIds = (parsed.Value.ClearRoleIds ?? new List<string>())
            .Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        if ((applyToRoles.Count > 0 || clearRoleIds.Count > 0) && !canApplyToRoles)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Forbidden", ["requiredFeatures"] = ManageFeatures }, 403);

        var existingUser = await svc.LoadSidebarPreferenceUpdatedAtAsync(auth.UserId, auth.TenantId, auth.OrganizationId);
        if (existingUser.HasValue)
        {
            var conflict = SidebarOptimisticLock.Check("auth.sidebar_preference", existingUser.Value.UpdatedAt, http.Request);
            if (conflict is not null) return SidebarHttp.Json(conflict, 409);
        }

        var settings = await svc.SaveSidebarPreferenceAsync(auth.UserId, auth.TenantId, auth.OrganizationId, locale, payload);

        var availableRoles = canApplyToRoles ? await svc.ListRolesInScopeAsync(auth.TenantId) : new List<Role>();
        var roleMap = availableRoles.ToDictionary(r => r.Id.ToString(), r => r, StringComparer.Ordinal);

        if (applyToRoles.Count > 0)
        {
            var missing = applyToRoles.Where(id => !roleMap.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Invalid roles", ["missing"] = missing }, 400);
        }

        var updatedRoleIds = new List<Guid>();
        var filteredClearRoleIds = new List<string>();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            foreach (var roleId in applyToRoles)
            {
                var role = roleMap[roleId];
                await svc.SaveRoleSidebarPreferenceAsync(role.Id, auth.TenantId, locale, payload);
                updatedRoleIds.Add(role.Id);
            }

            var clearTargets = clearRoleIds
                .Where(id => !updatedRoleIds.Any(u => u.ToString() == id) && !applyToRoles.Contains(id))
                .ToList();
            filteredClearRoleIds.AddRange(clearTargets);

            if (filteredClearRoleIds.Count > 0)
            {
                var clearGuids = filteredClearRoleIds
                    .Where(id => Guid.TryParse(id, out _)).Select(Guid.Parse).ToList();
                await svc.ClearRoleSidebarPreferencesAsync(clearGuids, auth.TenantId);
            }
            await tx.CommitAsync();
        }

        InvalidateCacheTags(); // PARITY-TODO: cache.deleteByTags(nav:sidebar:*) is a no-op here.

        var rolesPayloadOut = new List<SidebarRoleEntry>();
        if (canApplyToRoles)
        {
            var rolePrefs = await svc.LoadRoleSidebarPreferencesAsync(availableRoles.Select(r => r.Id).ToList(), auth.TenantId);
            rolesPayloadOut = availableRoles.Select(r => new SidebarRoleEntry(r.Id, r.Name, rolePrefs.ContainsKey(r.Id))).ToList();
        }

        var savedUserVersion = await svc.LoadSidebarPreferenceUpdatedAtAsync(auth.UserId, auth.TenantId, auth.OrganizationId);

        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["settings"] = settings.ToDict(),
            ["canApplyToRoles"] = canApplyToRoles,
            ["roles"] = rolesPayloadOut.Select(SidebarHttp.RoleEntry).ToList(),
            ["scope"] = new Dictionary<string, object?> { ["type"] = "user" },
            ["updatedAt"] = savedUserVersion.HasValue ? SidebarJson.ToIsoOrNull(savedUserVersion.Value.UpdatedAt) : null,
            ["appliedRoles"] = updatedRoleIds.Cast<object?>().ToList(),
            ["clearedRoles"] = filteredClearRoleIds.Cast<object?>().ToList(),
        });
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var svc = new SidebarPreferencesService(db);

        var roleIdParam = http.Request.Query["roleId"].ToString();
        if (string.IsNullOrEmpty(roleIdParam))
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "roleId query parameter is required" }, 400);

        var canApplyToRoles = await rbac.UserHasAllFeatures(auth.UserId, ManageFeatures, auth.TenantId, auth.OrganizationId);
        if (!canApplyToRoles)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Forbidden", ["requiredFeatures"] = ManageFeatures }, 403);

        var role = Guid.TryParse(roleIdParam, out var rid)
            ? await svc.FindRoleInScopeAsync(rid, auth.TenantId)
            : null;
        if (role is null)
            return SidebarHttp.Json(new Dictionary<string, object?> { ["error"] = "Role not found" }, 404);

        await svc.ClearRoleSidebarPreferencesAsync(new[] { role.Id }, auth.TenantId);
        InvalidateCacheTags(); // PARITY-TODO: cache.deleteByTags(['nav:sidebar:role:<id>']) no-op.

        return SidebarHttp.Json(new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["scope"] = new Dictionary<string, object?> { ["type"] = "role", ["roleId"] = role.Id },
        });
    }

    /// <summary>
    /// Route-level sanitization of user-scope preferences before persistence (mirrors the inline
    /// trimming/dedupe in the upstream PUT handler). The service re-normalizes on save.
    /// </summary>
    private static SidebarSettings BuildUserPayload(SidebarSettingsInput s)
    {
        var groupOrder = TrimDedupe(s.GroupOrder);
        var hiddenItems = TrimDedupe(s.HiddenItems);

        var itemOrder = new Dictionary<string, List<string>>();
        foreach (var kv in s.ItemOrder ?? new Dictionary<string, List<string>>())
        {
            var trimmedGroup = kv.Key.Trim();
            if (trimmedGroup.Length == 0) continue;
            var values = TrimDedupe(kv.Value);
            if (values.Count > 0) itemOrder[trimmedGroup] = values;
        }

        return new SidebarSettings
        {
            Version = s.Version ?? SidebarSettings.DefaultVersion,
            GroupOrder = groupOrder,
            GroupLabels = SanitizeRecord(s.GroupLabels),
            ItemLabels = SanitizeRecord(s.ItemLabels),
            HiddenItems = hiddenItems,
            ItemOrder = itemOrder,
        };
    }

    private static List<string> TrimDedupe(IEnumerable<string>? source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<string>();
        foreach (var raw in source ?? Enumerable.Empty<string>())
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            outList.Add(trimmed);
        }
        return outList;
    }

    private static Dictionary<string, string> SanitizeRecord(IReadOnlyDictionary<string, string>? record)
    {
        var outMap = new Dictionary<string, string>();
        if (record is null) return outMap;
        foreach (var kv in record)
        {
            var key = kv.Key.Trim();
            var value = kv.Value.Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            outMap[key] = value;
        }
        return outMap;
    }

    // PARITY-TODO: upstream invalidates `nav:sidebar:*` cache tags via the DI cache service. The .NET
    // port has no read-through nav cache yet, so this is intentionally a no-op.
    private static void InvalidateCacheTags() { }
}
