using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>A serialized sidebar variant (port of <c>SidebarVariantRecord</c>).</summary>
public sealed record SidebarVariantRecord(
    Guid Id,
    string Name,
    bool IsActive,
    SidebarSettings Settings,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>A role entry for the sidebar-preferences "roles" payload.</summary>
public sealed record SidebarRoleEntry(Guid Id, string Name, bool HasPreference);

/// <summary>
/// Port of <c>services/sidebarPreferencesService.ts</c> — persistence for per-user sidebar
/// preferences, per-role sidebar preferences, and named per-user variants. Instantiated per request
/// against the shared <see cref="AppDbContext"/>. Sidebar tables carry no encrypted columns, so the
/// upstream <c>findWithDecryption</c> calls become plain EF queries.
/// </summary>
public sealed class SidebarPreferencesService
{
    private readonly AppDbContext _db;

    public SidebarPreferencesService(AppDbContext db) => _db = db;

    // --- User preferences ------------------------------------------------------------------------

    public async Task<SidebarSettings> LoadSidebarPreferenceAsync(Guid userId, Guid? tenantId, Guid? organizationId)
    {
        var existing = await UserPrefQuery(userId, tenantId, organizationId).AsNoTracking().FirstOrDefaultAsync();
        return SidebarSettings.Parse(existing?.SettingsJson);
    }

    public async Task<(Guid Id, DateTimeOffset? UpdatedAt)?> LoadSidebarPreferenceUpdatedAtAsync(
        Guid userId, Guid? tenantId, Guid? organizationId)
    {
        var existing = await UserPrefQuery(userId, tenantId, organizationId).AsNoTracking()
            .Select(p => new { p.Id, p.UpdatedAt })
            .FirstOrDefaultAsync();
        return existing is null ? null : (existing.Id, existing.UpdatedAt);
    }

    public async Task<SidebarSettings> SaveSidebarPreferenceAsync(
        Guid userId, Guid? tenantId, Guid? organizationId, string locale, SidebarSettings input)
    {
        var normalized = SidebarSettings.Normalize(input);
        var pref = await UserPrefQuery(userId, tenantId, organizationId).FirstOrDefaultAsync();
        if (pref is null)
        {
            pref = new UserSidebarPreference
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantId = tenantId,
                OrganizationId = organizationId,
                Locale = locale,
                SettingsJson = normalized.ToJson(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = null, // onUpdate only, no onCreate
            };
            _db.Add(pref);
        }
        else
        {
            pref.SettingsJson = normalized.ToJson();
            pref.UpdatedAt = DateTimeOffset.UtcNow; // MikroORM onUpdate
        }
        await _db.SaveChangesAsync();
        return normalized;
    }

    private IQueryable<UserSidebarPreference> UserPrefQuery(Guid userId, Guid? tenantId, Guid? organizationId)
    {
        var q = _db.Set<UserSidebarPreference>().Where(p => p.UserId == userId);
        q = tenantId is { } t ? q.Where(p => p.TenantId == t) : q.Where(p => p.TenantId == null);
        q = organizationId is { } o ? q.Where(p => p.OrganizationId == o) : q.Where(p => p.OrganizationId == null);
        return q;
    }

    // --- Role preferences ------------------------------------------------------------------------

    /// <summary>Port of <c>loadRoleSidebarPreferences</c> incl. the tenant-specific-wins merge.</summary>
    public async Task<Dictionary<Guid, SidebarSettings>> LoadRoleSidebarPreferencesAsync(
        IReadOnlyList<Guid> roleIds, Guid? tenantId)
    {
        var map = new Dictionary<Guid, SidebarSettings>();
        if (roleIds.Count == 0) return map;

        var q = _db.Set<RoleSidebarPreference>().AsNoTracking().Where(p => roleIds.Contains(p.RoleId));
        q = tenantId is { } t
            ? q.Where(p => p.TenantId == t || p.TenantId == null)
            : q.Where(p => p.TenantId == null);
        var prefs = await q.ToListAsync();

        foreach (var pref in prefs)
        {
            var key = pref.RoleId;
            if (tenantId is { } tid)
            {
                var hasExisting = map.ContainsKey(key);
                // If we already picked a tenant-specific row, a null-tenant row must not override it.
                if (hasExisting && pref.TenantId == null) continue;
                if (!hasExisting || pref.TenantId == tid)
                    map[key] = SidebarSettings.Parse(pref.SettingsJson);
                continue;
            }
            map[key] = SidebarSettings.Parse(pref.SettingsJson);
        }
        return map;
    }

    public async Task<(Guid Id, DateTimeOffset? UpdatedAt)?> LoadRoleSidebarPreferenceUpdatedAtAsync(
        Guid roleId, Guid? tenantId)
    {
        var existing = await RolePrefQuery(roleId, tenantId).AsNoTracking()
            .Select(p => new { p.Id, p.UpdatedAt })
            .FirstOrDefaultAsync();
        return existing is null ? null : (existing.Id, existing.UpdatedAt);
    }

    public async Task<SidebarSettings> SaveRoleSidebarPreferenceAsync(
        Guid roleId, Guid? tenantId, string locale, SidebarSettings input)
    {
        var normalized = SidebarSettings.Normalize(input);
        var pref = await RolePrefQuery(roleId, tenantId).FirstOrDefaultAsync();
        if (pref is null)
        {
            pref = new RoleSidebarPreference
            {
                Id = Guid.NewGuid(),
                RoleId = roleId,
                TenantId = tenantId,
                Locale = locale,
                SettingsJson = normalized.ToJson(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = null,
            };
            _db.Add(pref);
        }
        else
        {
            pref.SettingsJson = normalized.ToJson();
            pref.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync();
        return normalized;
    }

    /// <summary>Hard-delete (nativeDelete) role prefs for the given roles + tenant scope. Idempotent.</summary>
    public async Task ClearRoleSidebarPreferencesAsync(IReadOnlyCollection<Guid> roleIds, Guid? tenantId)
    {
        if (roleIds.Count == 0) return;
        var q = _db.Set<RoleSidebarPreference>().Where(p => roleIds.Contains(p.RoleId));
        q = tenantId is { } t ? q.Where(p => p.TenantId == t) : q.Where(p => p.TenantId == null);
        await q.ExecuteDeleteAsync();
    }

    private IQueryable<RoleSidebarPreference> RolePrefQuery(Guid roleId, Guid? tenantId)
    {
        var q = _db.Set<RoleSidebarPreference>().Where(p => p.RoleId == roleId);
        return tenantId is { } t ? q.Where(p => p.TenantId == t) : q.Where(p => p.TenantId == null);
    }

    // --- Roles-in-scope helpers ------------------------------------------------------------------

    /// <summary>
    /// Roles visible to the tenant, ordered by name. Upstream also allows null-tenant (global) roles,
    /// but <c>roles.tenant_id</c> is NOT NULL post-migration so those never exist; when the actor has no
    /// tenant the scope resolves to nothing.
    /// </summary>
    public async Task<List<Role>> ListRolesInScopeAsync(Guid? tenantId)
    {
        if (tenantId is not { } t) return new List<Role>();
        return await _db.Set<Role>().AsNoTracking()
            .Where(r => r.TenantId == t)
            .OrderBy(r => r.Name)
            .ToListAsync();
    }

    /// <summary>Port of <c>findRoleInScope</c> incl. the defense-in-depth cross-tenant guard.</summary>
    public async Task<Role?> FindRoleInScopeAsync(Guid roleId, Guid? tenantId)
    {
        Role? role;
        if (tenantId is { } t)
            role = await _db.Set<Role>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == t);
        else
            // roles.tenant_id is NOT NULL, so a null-tenant scope matches nothing.
            role = null;
        if (role is null) return null;
        if (tenantId is { } tid && role.TenantId != tid) return null;
        return role;
    }

    public async Task<List<SidebarRoleEntry>> LoadRolesPayloadAsync(Guid? tenantId)
    {
        var roles = await ListRolesInScopeAsync(tenantId);
        if (roles.Count == 0) return new List<SidebarRoleEntry>();
        var prefs = await LoadRoleSidebarPreferencesAsync(roles.Select(r => r.Id).ToList(), tenantId);
        return roles.Select(r => new SidebarRoleEntry(r.Id, r.Name, prefs.ContainsKey(r.Id))).ToList();
    }

    // --- Named variants --------------------------------------------------------------------------

    public async Task<List<SidebarVariantRecord>> ListSidebarVariantsAsync(Guid userId, Guid? tenantId)
    {
        var variants = await VariantQuery(userId, tenantId).AsNoTracking()
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();
        return variants.Select(ToRecord).ToList();
    }

    public async Task<SidebarVariantRecord?> LoadSidebarVariantAsync(Guid userId, Guid? tenantId, Guid variantId)
    {
        var variant = await VariantQuery(userId, tenantId).AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == variantId);
        return variant is null ? null : ToRecord(variant);
    }

    /// <summary>Port of <c>nextVariantAutoName</c> (pure logic exposed for unit tests).</summary>
    public static string ComputeAutoName(IEnumerable<string> existingNames, string prefix = "My preferences")
    {
        var used = new HashSet<int>();
        var pattern = new Regex($"^{Regex.Escape(prefix)}\\s+(\\d+)$");
        foreach (var name in existingNames)
        {
            if (name == prefix) { used.Add(1); continue; }
            var match = pattern.Match(name);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n)) used.Add(n);
        }
        if (!used.Contains(1)) return prefix;
        var next = 2;
        while (used.Contains(next)) next += 1;
        return $"{prefix} {next}";
    }

    public async Task<string> NextVariantAutoNameAsync(Guid userId, Guid? tenantId, string prefix = "My preferences")
    {
        var names = await VariantQuery(userId, tenantId).AsNoTracking()
            .OrderBy(v => v.CreatedAt)
            .Select(v => v.Name)
            .ToListAsync();
        return ComputeAutoName(names, prefix);
    }

    public async Task<SidebarVariantRecord> CreateSidebarVariantAsync(
        Guid userId, Guid? tenantId, Guid? organizationId, string locale,
        string? name, SidebarSettings settingsInput, bool isActive)
    {
        var finalName = string.IsNullOrEmpty(name?.Trim())
            ? await NextVariantAutoNameAsync(userId, tenantId)
            : name!.Trim();
        var settings = SidebarSettings.Normalize(settingsInput);

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (isActive)
            await DeactivateAllVariantsAsync(userId, tenantId, null);

        var variant = new SidebarVariant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantId = tenantId,
            OrganizationId = organizationId,
            Locale = locale,
            Name = finalName,
            SettingsJson = settings.ToJson(),
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = null,
        };
        _db.Add(variant);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return ToRecord(variant);
    }

    public async Task<SidebarVariantRecord?> UpdateSidebarVariantAsync(
        Guid userId, Guid? tenantId, Guid variantId,
        string? name, SidebarSettings? settingsInput, bool? isActive)
    {
        var variant = await VariantQuery(userId, tenantId).FirstOrDefaultAsync(v => v.Id == variantId);
        if (variant is null) return null;

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (!string.IsNullOrEmpty(name?.Trim()))
            variant.Name = name!.Trim();
        if (settingsInput is not null)
            variant.SettingsJson = SidebarSettings.Normalize(settingsInput).ToJson();
        if (isActive.HasValue)
            variant.IsActive = isActive.Value;
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        if (isActive == true)
            await DeactivateAllVariantsAsync(userId, tenantId, variantId);
        await tx.CommitAsync();
        return ToRecord(variant);
    }

    public async Task<bool> DeleteSidebarVariantAsync(Guid userId, Guid? tenantId, Guid variantId)
    {
        var variant = await VariantQuery(userId, tenantId).FirstOrDefaultAsync(v => v.Id == variantId);
        if (variant is null) return false;
        variant.DeletedAt = DateTimeOffset.UtcNow;
        variant.IsActive = false;
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task DeactivateAllVariantsAsync(Guid userId, Guid? tenantId, Guid? exceptId)
    {
        var q = _db.Set<SidebarVariant>().Where(v => v.UserId == userId && v.IsActive && v.DeletedAt == null);
        q = tenantId is { } t ? q.Where(v => v.TenantId == t) : q.Where(v => v.TenantId == null);
        if (exceptId is { } ex) q = q.Where(v => v.Id != ex);
        await q.ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));
    }

    private IQueryable<SidebarVariant> VariantQuery(Guid userId, Guid? tenantId)
    {
        var q = _db.Set<SidebarVariant>().Where(v => v.UserId == userId && v.DeletedAt == null);
        return tenantId is { } t ? q.Where(v => v.TenantId == t) : q.Where(v => v.TenantId == null);
    }

    private static SidebarVariantRecord ToRecord(SidebarVariant v) => new(
        v.Id, v.Name, v.IsActive == true, SidebarSettings.Parse(v.SettingsJson), v.CreatedAt, v.UpdatedAt);

    /// <summary>True when an <see cref="Exception"/> chain carries a Postgres unique-violation (SQLSTATE 23505).</summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var sqlState = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
            if (sqlState == "23505") return true;
        }
        return false;
    }
}
