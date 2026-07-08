using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>
/// Seed/init integration for install-from-CE. Call <see cref="InstallForAllTenantsAsync"/> after tenant
/// provisioning (e.g. from the initial-tenant seeder) so a fresh tenant's records immediately have their
/// module-declared custom-field definitions available — the .NET analogue of upstream running
/// <c>installCustomEntitiesFromModules</c> during <c>mercato init</c>.
/// </summary>
public static class InitTenantIntegration
{
    /// <summary>Install all module-declared field sets for every non-deleted tenant (idempotent).</summary>
    public static async Task<InstallCeResult> InstallForAllTenantsAsync(
        AppDbContext db, ModuleRegistry registry, CancellationToken ct = default)
    {
        var tenantIds = await db.Database.SqlQueryRaw<Guid>("SELECT id FROM tenants WHERE deleted_at IS NULL").ToListAsync(ct);
        var total = new InstallCeResult(0, 0, 0);
        foreach (var tenantId in tenantIds)
            total += await InstallFromCe.InstallAsync(db, registry, tenantId, organizationId: null, ct: ct);
        return total;
    }
}
