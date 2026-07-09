using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Data;

namespace OpenMercato.Core.Modules;

/// <summary>
/// Runs every registered module's setup hooks (<see cref="IModule.OnTenantCreatedAsync"/> /
/// <see cref="IModule.SeedDefaultsAsync"/> / <see cref="IModule.SeedExamplesAsync"/>) for each
/// existing (tenant, organization) scope — the .NET port of the per-module seed loops in
/// upstream <c>mercato init</c> (packages/cli/src/mercato.ts): modules are iterated in registration
/// (= dependency) order, and within a phase every module runs before advancing to the next phase.
///
/// This complements <c>InitialTenantSeeder</c> (the port of the core <c>setupInitialTenant</c> that
/// provisions the tenant/org/users/roles/ACLs). Provisioning runs first; then this runner drives the
/// modules' own data seeding. It intentionally lives in Core and depends only on <see cref="IModule"/>,
/// so it never references any concrete module.
/// </summary>
public static class ModuleSeedRunner
{
    /// <summary>
    /// Seed every module for every (tenant, org) scope currently present. Idempotent — each module's
    /// hooks guard their own rows. Phases run scope-by-scope: onTenantCreated → seedDefaults →
    /// seedExamples, mirroring upstream (onTenantCreated fires in setupInitialTenant, the two seed
    /// loops fire after). <paramref name="includeExamples"/> maps to upstream's <c>--no-examples</c>.
    /// </summary>
    public static async Task RunAsync(
        IServiceProvider services,
        ILogger logger,
        bool includeExamples = true,
        CancellationToken ct = default)
    {
        List<OrgScopeRow> scopes;
        var registry = services.GetRequiredService<ModuleRegistry>();
        using (var probe = services.CreateScope())
        {
            var db = probe.ServiceProvider.GetRequiredService<AppDbContext>();
            scopes = await db.Database.SqlQueryRaw<OrgScopeRow>(
                "SELECT tenant_id AS \"TenantId\", id AS \"OrganizationId\" FROM organizations WHERE deleted_at IS NULL ORDER BY depth, created_at")
                .ToListAsync(ct);
        }

        if (scopes.Count == 0)
        {
            logger.LogInformation("Module seed: no organization scopes found — nothing to seed.");
            return;
        }

        foreach (var s in scopes)
        {
            // One request-scoped provider per (tenant, org); every module seeding this scope shares it.
            using var scope = services.CreateScope();
            var ctx = new ModuleSeedContext(scope.ServiceProvider, s.TenantId, s.OrganizationId, includeExamples, ct);

            foreach (var module in registry.Modules)
                await module.OnTenantCreatedAsync(ctx);
            foreach (var module in registry.Modules)
                await module.SeedDefaultsAsync(ctx);
            if (includeExamples)
                foreach (var module in registry.Modules)
                    await module.SeedExamplesAsync(ctx);
        }

        logger.LogInformation(
            "Module seed: ran setup hooks for {ModuleCount} module(s) across {ScopeCount} organization scope(s) (examples: {Examples}).",
            registry.Modules.Count, scopes.Count, includeExamples);
    }

    private sealed record OrgScopeRow(Guid TenantId, Guid OrganizationId);
}
