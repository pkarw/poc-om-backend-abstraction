using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Lib;

namespace OpenMercato.Modules.Directory.Seeding;

/// <summary>Options for <see cref="InitialTenantSeeder"/>, mirroring the upstream
/// <c>auth setup</c> CLI flags (orgName / email / password / orgSlug).</summary>
public sealed record SetupInitialTenantOptions
{
    public string OrgName { get; init; } = InitialTenantSeeder.DefaultOrgName;
    public string Email { get; init; } = InitialTenantSeeder.DefaultSuperadminEmail;
    public string Password { get; init; } = InitialTenantSeeder.DefaultPassword;
    public string? OrgSlug { get; init; } = InitialTenantSeeder.DefaultOrgSlug;
    /// <summary>Role names ensured for the tenant. Defaults to employee/admin/superadmin.</summary>
    public IReadOnlyList<string>? RoleNames { get; init; }
}

/// <summary>A user touched by the seeder (id + plaintext email + role names + whether it was created).</summary>
public sealed record SeededUser(Guid Id, string Email, IReadOnlyList<string> Roles, bool Created);

/// <summary>Result of <see cref="InitialTenantSeeder.SetupInitialTenantAsync"/>.</summary>
public sealed record SetupInitialTenantResult(
    Guid TenantId,
    Guid OrganizationId,
    IReadOnlyList<SeededUser> Users,
    bool ReusedExistingUser);

/// <summary>
/// Open-Mercato-identical initial-tenant provisioning — the .NET port of
/// <c>auth/lib/setup-app.ts</c> (setupInitialTenant + ensureRoles + ensureDefaultRoleAcls) as run by
/// upstream <c>mercato init</c> / <c>auth setup</c>. Produces the exact Acme dataset:
///   • Tenant "&lt;orgName&gt; Tenant" + a single root Organization (slug, is_active, depth 0, hierarchy
///     arrays materialized by <see cref="OrganizationHierarchy"/>).
///   • Roles employee/admin/superadmin (tenant-scoped).
///   • Users superadmin@/admin@/employee@&lt;domain&gt; (bcrypt cost 10, is_confirmed, encrypted email +
///     email_hash), honoring the OM_INIT_* overrides.
///   • RoleAcls: superadmin → is_super_admin=true (+ superadmin default features); admin → the merged
///     module admin features (auth.* + directory.organizations.*); employee → empty feature set.
/// Idempotent: if the primary user already exists it reuses that tenant and only ensures
/// roles/ACLs. Parents are saved before children so the DB-level FKs (created by the raw-SQL
/// migrations) are satisfied even though the EF model declares no relationships.
///
/// This lives in the Directory module because it is the only ported module that can reference BOTH
/// the auth entities/crypto AND the directory entities/hierarchy (Directory → Auth → Core). See
/// ADR 0013.
/// </summary>
public static class InitialTenantSeeder
{
    public const string DefaultOrgName = "Acme Corp";
    public const string DefaultOrgSlug = "acme";
    public const string DefaultSuperadminEmail = "superadmin@acme.com";
    public const string DefaultPassword = "secret";
    private const string DefaultDerivedEmailDomain = "acme.com";

    /// <summary>DEFAULT_ROLE_NAMES from upstream (creation order preserved).</summary>
    public static readonly IReadOnlyList<string> DefaultRoleNames = new[] { "employee", "admin", "superadmin" };

    private static readonly string[] BuiltInRoles = { "superadmin", "admin", "employee" };

    public static async Task<SetupInitialTenantResult> SetupInitialTenantAsync(
        AppDbContext db,
        ModuleRegistry registry,
        PasswordHasher hasher,
        EncryptionService encryption,
        SetupInitialTenantOptions options,
        CancellationToken ct = default)
    {
        var roleNames = (options.RoleNames is { Count: > 0 } ? options.RoleNames : DefaultRoleNames).ToList();
        var primaryEmail = options.Email;

        // Idempotency / reuse: match the primary user by deterministic email hash (email is encrypted).
        var lookup = encryption.EmailHashLookupValues(primaryEmail);
        var existingUser = await db.Set<User>()
            .FirstOrDefaultAsync(u => u.EmailHash != null && lookup.Contains(u.EmailHash), ct);

        if (existingUser is not null)
        {
            if (existingUser.TenantId is not { } reusedTenantId)
                throw new InvalidOperationException("Cannot reuse a user without a tenantId — global roles are not supported.");

            await EnsureRolesAsync(db, roleNames, reusedTenantId, ct);
            await EnsureDefaultRoleAclsAsync(db, registry, reusedTenantId, ct);

            var reusedRoles = await RoleNamesForUserAsync(db, existingUser.Id, ct);
            return new SetupInitialTenantResult(
                reusedTenantId,
                existingUser.OrganizationId ?? Guid.Empty,
                new[] { new SeededUser(existingUser.Id, primaryEmail, reusedRoles, false) },
                ReusedExistingUser: true);
        }

        var now = DateTimeOffset.UtcNow;

        // 1) Tenant + root organization (parent before child for the tenant_id FK).
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = $"{options.OrgName} Tenant",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<Tenant>().Add(tenant);
        await db.SaveChangesAsync(ct);

        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = options.OrgName,
            Slug = string.IsNullOrWhiteSpace(options.OrgSlug) ? null : options.OrgSlug,
            IsActive = true,
            Depth = 0,
            AncestorIdsJson = "[]",
            ChildIdsJson = "[]",
            DescendantIdsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<Organization>().Add(organization);
        await db.SaveChangesAsync(ct);

        // 2) Roles for the tenant.
        await EnsureRolesAsync(db, roleNames, tenant.Id, ct);

        // 3) Users (primary superadmin + derived admin/employee), deduped by lowercase email.
        var domain = ExtractDomain(primaryEmail);
        var adminEmail = ReadEnv("OM_INIT_ADMIN_EMAIL") ?? $"admin@{domain}";
        var employeeEmail = ReadEnv("OM_INIT_EMPLOYEE_EMAIL") ?? $"employee@{domain}";
        var adminPassword = ReadEnv("OM_INIT_ADMIN_PASSWORD") ?? DefaultPassword;
        var employeePassword = ReadEnv("OM_INIT_EMPLOYEE_PASSWORD") ?? DefaultPassword;

        var baseUsers = new List<(string Email, string[] Roles, string Password)>();
        AddUnique(baseUsers, (primaryEmail, new[] { "superadmin" }, options.Password));
        AddUnique(baseUsers, (adminEmail, new[] { "admin" }, adminPassword));
        AddUnique(baseUsers, (employeeEmail, new[] { "employee" }, employeePassword));

        var snapshots = new List<SeededUser>();
        foreach (var spec in baseUsers)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                OrganizationId = organization.Id,
                Email = encryption.Encrypt(spec.Email)!,
                EmailHash = encryption.ComputeEmailHash(spec.Email),
                PasswordHash = hasher.Hash(spec.Password),
                IsConfirmed = true,
                CreatedAt = now,
            };
            db.Set<User>().Add(user);
            await db.SaveChangesAsync(ct);

            foreach (var roleName in spec.Roles)
            {
                var role = await FindRoleAsync(db, roleName, tenant.Id, ct)
                    ?? throw new InvalidOperationException($"ROLE_NOT_FOUND:{roleName}");
                db.Set<UserRole>().Add(new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    RoleId = role.Id,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);
            snapshots.Add(new SeededUser(user.Id, spec.Email, spec.Roles, true));
        }

        // 4) Materialize the org hierarchy (single root: root_id=self, tree_path=id, depth 0).
        await OrganizationHierarchy.RebuildForTenantAsync(db, tenant.Id, ct);

        // 5) Role ACLs from merged module defaultRoleFeatures.
        await EnsureDefaultRoleAclsAsync(db, registry, tenant.Id, ct);

        // 6) directory seedDefaults parity: backfill any slugless org (no-op when orgSlug was given).
        await DirectorySeeder.BackfillOrganizationSlugsAsync(db, tenant.Id, ct);

        return new SetupInitialTenantResult(tenant.Id, organization.Id, snapshots, ReusedExistingUser: false);
    }

    /// <summary>
    /// Env-gated, idempotent boot seeder (replaces the old minimal AuthBootstrapSeeder). Runs from the
    /// API host after migrations: when OM_INIT_SUPERADMIN_EMAIL + OM_INIT_SUPERADMIN_PASSWORD are set
    /// AND no users exist, provisions the full Acme dataset — identical to CLI <c>init</c>/<c>seed</c>.
    /// </summary>
    public static async Task RunBootAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var email = ReadEnv("OM_INIT_SUPERADMIN_EMAIL");
        var password = ReadEnv("OM_INIT_SUPERADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return; // not configured — nothing to do

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        if (await db.Set<User>().AnyAsync(ct))
            return; // idempotent — users already exist

        var result = await SetupInitialTenantAsync(db, registry, hasher, encryption, new SetupInitialTenantOptions
        {
            OrgName = ReadEnv("OM_INIT_ORG_NAME") ?? DefaultOrgName,
            OrgSlug = ReadEnv("OM_INIT_ORG_SLUG") ?? DefaultOrgSlug,
            Email = email!,
            Password = password!,
        }, ct);

        logger.LogInformation(
            "Init seed: tenant {TenantId}, organization {OrgId}, {UserCount} users, roles [{Roles}].",
            result.TenantId, result.OrganizationId, result.Users.Count, string.Join(",", DefaultRoleNames));
    }

    // --- role / ACL helpers (ports of ensureRoles / ensureDefaultRoleAcls / ensureRoleAclFor) ------

    private static async Task EnsureRolesAsync(AppDbContext db, IReadOnlyList<string> roleNames, Guid tenantId, CancellationToken ct)
    {
        var added = false;
        foreach (var name in roleNames)
        {
            if (await FindRoleAsync(db, name, tenantId, ct) is not null) continue;
            db.Set<Role>().Add(new Role { Id = Guid.NewGuid(), Name = name, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
            added = true;
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureDefaultRoleAclsAsync(AppDbContext db, ModuleRegistry registry, Guid tenantId, CancellationToken ct)
    {
        var merged = registry.MergedDefaultRoleFeatures;
        string[] Features(string role) => merged.TryGetValue(role, out var f) ? f.ToArray() : Array.Empty<string>();

        var superadmin = await FindRoleAsync(db, "superadmin", tenantId, ct);
        var admin = await FindRoleAsync(db, "admin", tenantId, ct);
        var employee = await FindRoleAsync(db, "employee", tenantId, ct);

        if (superadmin is not null)
            await EnsureRoleAclForAsync(db, superadmin, tenantId, Features("superadmin"), isSuperAdmin: true, ct);
        if (admin is not null)
            await EnsureRoleAclForAsync(db, admin, tenantId, Features("admin"), isSuperAdmin: false, ct);
        if (employee is not null)
            await EnsureRoleAclForAsync(db, employee, tenantId, Features("employee"), isSuperAdmin: false, ct);

        // Custom roles declared by modules (any role key that is not a built-in).
        foreach (var (roleName, features) in merged)
        {
            if (BuiltInRoles.Contains(roleName)) continue;
            var role = await FindRoleAsync(db, roleName, tenantId, ct);
            if (role is not null)
                await EnsureRoleAclForAsync(db, role, tenantId, features.ToArray(), isSuperAdmin: false, ct);
        }
    }

    private static async Task EnsureRoleAclForAsync(
        AppDbContext db, Role role, Guid tenantId, string[] features, bool isSuperAdmin, CancellationToken ct)
    {
        var existing = await db.Set<RoleAcl>()
            .FirstOrDefaultAsync(a => a.RoleId == role.Id && a.TenantId == tenantId, ct);
        if (existing is null)
        {
            db.Set<RoleAcl>().Add(new RoleAcl
            {
                Id = Guid.NewGuid(),
                RoleId = role.Id,
                TenantId = tenantId,
                FeaturesJson = JsonArray.Serialize(features),
                IsSuperAdmin = isSuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var current = JsonArray.Parse(existing.FeaturesJson) ?? Array.Empty<string>();
        var mergedFeatures = current.Concat(features).Distinct().ToArray();
        var changed = mergedFeatures.Length != current.Length;
        if (changed) existing.FeaturesJson = JsonArray.Serialize(mergedFeatures);
        if (isSuperAdmin && !existing.IsSuperAdmin) { existing.IsSuperAdmin = true; changed = true; }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static Task<Role?> FindRoleAsync(AppDbContext db, string name, Guid tenantId, CancellationToken ct) =>
        db.Set<Role>().FirstOrDefaultAsync(r => r.Name == name && r.TenantId == tenantId, ct);

    private static async Task<IReadOnlyList<string>> RoleNamesForUserAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        await (from ur in db.Set<UserRole>()
               join r in db.Set<Role>() on ur.RoleId equals r.Id
               where ur.UserId == userId
               select r.Name).ToListAsync(ct);

    // --- misc helpers -----------------------------------------------------------------------------

    private static void AddUnique(List<(string Email, string[] Roles, string Password)> users, (string Email, string[] Roles, string Password) entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Email)) return;
        if (users.Any(u => string.Equals(u.Email, entry.Email, StringComparison.OrdinalIgnoreCase))) return;
        users.Add(entry);
    }

    private static string ExtractDomain(string email)
    {
        var at = email.IndexOf('@');
        var domain = at >= 0 && at < email.Length - 1 ? email[(at + 1)..] : null;
        return string.IsNullOrWhiteSpace(domain) ? DefaultDerivedEmailDomain : domain;
    }

    private static string? ReadEnv(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
