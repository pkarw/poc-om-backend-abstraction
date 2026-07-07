using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;

namespace OpenMercato.Modules.Dashboards.Seeding;

/// <summary>
/// Widget-availability seeding — the .NET port of upstream cli.ts <c>seedDashboardDefaultsForTenant</c>
/// and lib/role-widgets.ts <c>appendWidgetsToRoles</c>. Invoked by the dashboards CLI commands
/// (seed-defaults / enable-analytics-widgets). Not wired into the boot seeder: the layout GET already
/// returns a valid default envelope without any role-widget records, and Directory (Tier-1) must not
/// depend on Dashboards (Tier-3) — see ADR 0014.
/// </summary>
public static class DashboardSeeder
{
    /// <summary>
    /// Upsert <c>dashboard_role_widgets</c> for the given roles: admin/superadmin get ALL widget ids,
    /// other roles get the <c>defaultEnabled</c> ids (currently none); an explicit widgetIds list
    /// overrides both. Roles with an empty resolved set are skipped (no record created).
    /// </summary>
    public static async Task<bool> SeedDefaultsForTenantAsync(
        AppDbContext db,
        Guid tenantId,
        Guid? organizationId = null,
        IReadOnlyList<string>? roleNames = null,
        IReadOnlyList<string>? widgetIds = null,
        CancellationToken ct = default)
    {
        roleNames ??= new[] { "superadmin", "admin", "employee" };
        var validIds = WidgetCatalog.AllIds().ToHashSet();
        var resolvedWidgetIds = widgetIds is { Count: > 0 } ? widgetIds.Where(validIds.Contains).ToList() : null;
        var defaultWidgetIds = WidgetCatalog.DefaultEnabledIds();
        var allWidgetIds = WidgetCatalog.AllIds();

        if (resolvedWidgetIds is not null && resolvedWidgetIds.Count == 0) return false;

        foreach (var roleName in roleNames)
        {
            var isAdminRole = roleName is "admin" or "superadmin";
            var roleWidgetIds = resolvedWidgetIds ?? (isAdminRole ? allWidgetIds : defaultWidgetIds);
            if (roleWidgetIds.Count == 0) continue;

            var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == roleName && r.TenantId == tenantId, ct);
            if (role is null) continue;

            var existing = await db.Set<DashboardRoleWidgets>()
                .FirstOrDefaultAsync(r => r.RoleId == role.Id && r.TenantId == tenantId
                    && r.OrganizationId == organizationId && r.DeletedAt == null, ct);
            if (existing is not null)
            {
                existing.WidgetIdsJson = JsonStrings.SerializeArray(roleWidgetIds);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.Set<DashboardRoleWidgets>().Add(new DashboardRoleWidgets
                {
                    Id = Guid.NewGuid(),
                    RoleId = role.Id,
                    TenantId = tenantId,
                    OrganizationId = organizationId,
                    WidgetIdsJson = JsonStrings.SerializeArray(roleWidgetIds),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Append widget ids (append-only) to the existing role records for the given roles within a
    /// scope. Falls back to the org-null role record when an org-scoped one is absent. Roles without
    /// an existing record are skipped (parity with upstream appendWidgetsToRoles).
    /// </summary>
    public static async Task<bool> AppendWidgetsToRolesAsync(
        AppDbContext db,
        Guid tenantId,
        Guid? organizationId,
        IReadOnlyList<string> roleNames,
        IReadOnlyList<string> widgetIds,
        CancellationToken ct = default)
    {
        var validIds = WidgetCatalog.AllIds().ToHashSet();
        var resolved = widgetIds.Where(validIds.Contains).ToList();
        if (resolved.Count == 0) return false;

        var updated = false;
        foreach (var roleName in roleNames)
        {
            var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == roleName && r.TenantId == tenantId, ct)
                ?? await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == roleName, ct);
            if (role is null) continue;

            var record = await db.Set<DashboardRoleWidgets>()
                .FirstOrDefaultAsync(r => r.RoleId == role.Id && r.TenantId == tenantId
                    && r.OrganizationId == organizationId && r.DeletedAt == null, ct);
            if (record is null && organizationId is not null)
                record = await db.Set<DashboardRoleWidgets>()
                    .FirstOrDefaultAsync(r => r.RoleId == role.Id && r.TenantId == tenantId
                        && r.OrganizationId == null && r.DeletedAt == null, ct);
            if (record is null) continue;

            var current = JsonStrings.ParseArray(record.WidgetIdsJson);
            var existing = current.ToHashSet();
            var next = new List<string>(current);
            foreach (var id in resolved)
                if (existing.Add(id)) next.Add(id);
            if (next.Count == current.Count) continue;

            record.WidgetIdsJson = JsonStrings.SerializeArray(next);
            record.UpdatedAt = DateTimeOffset.UtcNow;
            updated = true;
        }

        if (updated) await db.SaveChangesAsync(ct);
        return updated;
    }
}
