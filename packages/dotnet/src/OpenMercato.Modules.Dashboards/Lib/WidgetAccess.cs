using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Services;
using OpenMercato.Modules.Dashboards.Data;

namespace OpenMercato.Modules.Dashboards.Lib;

/// <summary>The widget-availability resolution context (upstream lib/access.ts AccessContext).</summary>
public sealed record WidgetAccessContext(
    Guid UserId,
    Guid? TenantId,
    Guid? OrganizationId,
    IReadOnlyList<string> Features,
    bool IsSuperAdmin);

/// <summary>
/// Widget-availability engine — a 1:1 port of upstream <c>lib/access.ts</c>
/// <c>resolveAllowedWidgetIds</c>. Resolution order: a per-user <c>override</c> record
/// (empty override ⇒ hide everything) → the union of the user's most-specific role records →
/// otherwise all widgets; the base set is then filtered to widgets whose feature requirements the
/// user satisfies (superadmin bypasses).
/// </summary>
public static class WidgetAccess
{
    private static int Specificity(DashboardRoleWidgets r) =>
        (r.TenantId.HasValue ? 1 : 0) + (r.OrganizationId.HasValue ? 2 : 0);

    public static async Task<List<string>> ResolveAllowedWidgetIds(
        AppDbContext db,
        WidgetAccessContext ctx,
        IReadOnlyList<DashboardWidget> widgets,
        CancellationToken ct = default)
    {
        var allWidgetIds = widgets.Select(w => w.Id).ToHashSet();

        // 1) User override (if any) for the exact scope.
        var userRecord = await db.Set<DashboardUserWidgets>().AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserId == ctx.UserId && u.TenantId == ctx.TenantId
                && u.OrganizationId == ctx.OrganizationId && u.DeletedAt == null, ct);

        HashSet<string>? allowedByUser = null;
        if (userRecord is not null && userRecord.Mode == "override")
            allowedByUser = JsonStrings.ParseArray(userRecord.WidgetIdsJson).Where(allWidgetIds.Contains).ToHashSet();

        if (allowedByUser is not null && allowedByUser.Count == 0)
            return new List<string>();

        // 2) Aggregate role-level settings (most-specific matching record per role).
        var roleIds = await db.Set<UserRole>().AsNoTracking()
            .Where(ur => ur.UserId == ctx.UserId && ur.DeletedAt == null)
            .Select(ur => ur.RoleId)
            .Distinct()
            .ToListAsync(ct);

        var roleRecords = roleIds.Count == 0
            ? new List<DashboardRoleWidgets>()
            : await db.Set<DashboardRoleWidgets>().AsNoTracking()
                .Where(r => roleIds.Contains(r.RoleId) && r.DeletedAt == null)
                .ToListAsync(ct);

        var byRole = new Dictionary<Guid, DashboardRoleWidgets>();
        foreach (var record in roleRecords)
        {
            // Skip records whose scope conflicts with the request scope (upstream conflict filter).
            if (record.TenantId.HasValue && ctx.TenantId.HasValue && record.TenantId != ctx.TenantId) continue;
            if (record.TenantId.HasValue && !ctx.TenantId.HasValue) continue;
            if (record.OrganizationId.HasValue && ctx.OrganizationId.HasValue && record.OrganizationId != ctx.OrganizationId) continue;
            if (record.OrganizationId.HasValue && !ctx.OrganizationId.HasValue) continue;
            if (!byRole.TryGetValue(record.RoleId, out var current) || Specificity(record) > Specificity(current))
                byRole[record.RoleId] = record;
        }

        var allowedByRole = new HashSet<string>();
        foreach (var record in byRole.Values)
            foreach (var id in JsonStrings.ParseArray(record.WidgetIdsJson))
                if (allWidgetIds.Contains(id)) allowedByRole.Add(id);

        // 3) Base set.
        HashSet<string> baseSet;
        if (allowedByUser is not null) baseSet = allowedByUser;
        else if (allowedByRole.Count > 0) baseSet = allowedByRole;
        else baseSet = allWidgetIds.ToHashSet();

        if (baseSet.Count == 0) return new List<string>();

        // 4) Feature filter (superadmin bypasses).
        return widgets
            .Where(w => baseSet.Contains(w.Id) && (ctx.IsSuperAdmin || FeatureMatch.HasAll(w.Features, ctx.Features)))
            .Select(w => w.Id)
            .ToList();
    }
}
