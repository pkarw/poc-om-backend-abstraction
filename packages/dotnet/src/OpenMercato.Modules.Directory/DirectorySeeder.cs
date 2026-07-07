using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Lib;

namespace OpenMercato.Modules.Directory;

/// <summary>
/// Small reusable seeding helpers the bootstrap seeder + auth can call. Reproduces enough of
/// upstream setup.ts (backfillOrganizationSlugs) plus a single-root-org provisioner that runs the
/// OrganizationHierarchy engine so the org materialized tree (depth/ancestor/descendant arrays) is
/// consistent for a freshly-seeded tenant.
/// </summary>
public static class DirectorySeeder
{
    /// <summary>
    /// Ensure the given tenant row exists (id-keyed) and provision a single root organization when
    /// the tenant has none, then rebuild the hierarchy. Idempotent. Returns the root org id.
    /// </summary>
    public static async Task<Guid> EnsureTenantWithRootOrgAsync(
        AppDbContext db, Guid tenantId, string tenantName, string orgName, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = await db.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            tenant = new Tenant { Id = tenantId, Name = tenantName, IsActive = true, CreatedAt = now, UpdatedAt = now };
            db.Set<Tenant>().Add(tenant);
            await db.SaveChangesAsync(ct);
        }

        var existing = await db.Set<Organization>()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.DeletedAt == null, ct);
        if (existing is not null) return existing.Id;

        var orgId = Guid.NewGuid();
        var slug = Slugify.Run(orgName);
        var org = new Organization
        {
            Id = orgId, TenantId = tenantId, Name = orgName,
            Slug = string.IsNullOrEmpty(slug) ? null : slug,
            IsActive = true, RootId = orgId, TreePath = orgId.ToString(),
            CreatedAt = now, UpdatedAt = now,
        };
        db.Set<Organization>().Add(org);
        await db.SaveChangesAsync(ct);

        await OrganizationHierarchy.RebuildForTenantAsync(db, tenantId, ct);
        await BackfillOrganizationSlugsAsync(db, tenantId, ct);
        return orgId;
    }

    /// <summary>1:1 port of setup.ts backfillOrganizationSlugs (dedupe by appending -1, -2, …).</summary>
    public static async Task BackfillOrganizationSlugsAsync(AppDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var slugless = await db.Set<Organization>()
            .Where(o => o.TenantId == tenantId && o.Slug == null && o.DeletedAt == null).ToListAsync(ct);
        if (slugless.Count == 0) return;

        var existing = (await db.Set<Organization>().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.DeletedAt == null && o.Slug != null)
            .Select(o => o.Slug!).ToListAsync(ct)).ToHashSet();

        foreach (var org in slugless)
        {
            var baseSlug = Slugify.Run(org.Name);
            if (string.IsNullOrEmpty(baseSlug)) continue;
            var candidate = baseSlug;
            var suffix = 0;
            while (existing.Contains(candidate)) { suffix++; candidate = $"{baseSlug}-{suffix}"; }
            org.Slug = candidate;
            existing.Add(candidate);
        }
        await db.SaveChangesAsync(ct);
    }
}
