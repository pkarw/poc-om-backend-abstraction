using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Directory;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Seeding;
using Xunit;

namespace OpenMercato.Tests.Directory;

/// <summary>
/// Verifies the OM-parity <see cref="InitialTenantSeeder"/> produces the exact Acme dataset
/// (1 tenant, 1 org, 3 roles, 3 users, correct ACLs) and is idempotent. Pure EF InMemory — no live DB.
/// </summary>
public sealed class InitialTenantSeederTests
{
    private static ModuleRegistry Registry() =>
        new(new IModule[] { new AuthModule(), new DirectoryModule() });

    private static AppDbContext CreateDb(ModuleRegistry registry) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"seed-tests-{Guid.NewGuid():N}").Options,
            registry);

    private static (PasswordHasher hasher, EncryptionService encryption) Crypto() =>
        (new PasswordHasher(), new EncryptionService(AppConfig.FromEnvironment()));

    [Fact]
    public async Task Seeds_full_acme_dataset()
    {
        var registry = Registry();
        await using var db = CreateDb(registry);
        var (hasher, encryption) = Crypto();

        var result = await InitialTenantSeeder.SetupInitialTenantAsync(
            db, registry, hasher, encryption, new SetupInitialTenantOptions());

        Assert.False(result.ReusedExistingUser);

        // Tenant + org.
        var tenant = Assert.Single(await db.Set<Tenant>().ToListAsync());
        Assert.Equal("Acme Corp Tenant", tenant.Name);
        Assert.True(tenant.IsActive);

        var org = Assert.Single(await db.Set<Organization>().ToListAsync());
        Assert.Equal("Acme Corp", org.Name);
        Assert.Equal("acme", org.Slug);
        Assert.True(org.IsActive);
        Assert.Equal(0, org.Depth);
        Assert.Equal(org.Id, org.RootId);       // single root → self-root after hierarchy rebuild
        Assert.Equal(tenant.Id, org.TenantId);

        // 3 roles.
        var roles = await db.Set<Role>().ToListAsync();
        Assert.Equal(3, roles.Count);
        Assert.Equal(new[] { "admin", "employee", "superadmin" }, roles.Select(r => r.Name).OrderBy(n => n));
        Assert.All(roles, r => Assert.Equal(tenant.Id, r.TenantId));

        // 3 users, emails decrypt back to the Acme identities, all confirmed with bcrypt hashes.
        var users = await db.Set<User>().ToListAsync();
        Assert.Equal(3, users.Count);
        var emails = users.Select(u => u.Email).OrderBy(e => e).ToArray();
        Assert.Equal(new[] { "admin@acme.com", "employee@acme.com", "superadmin@acme.com" }, emails);
        Assert.All(users, u =>
        {
            Assert.True(u.IsConfirmed);
            Assert.StartsWith("$2", u.PasswordHash);          // bcrypt
            Assert.Equal(tenant.Id, u.TenantId);
            Assert.Equal(org.Id, u.OrganizationId);
        });

        // Password 'secret' verifies against the superadmin hash.
        var superUser = users.Single(u => u.Email == "superadmin@acme.com");
        Assert.True(hasher.Verify(superUser.PasswordHash, "secret"));

        // Role links: each user has exactly its own role.
        async Task<string> RoleOf(string email)
        {
            var user = users.Single(u => u.Email == email);
            var roleId = (await db.Set<UserRole>().Where(ur => ur.UserId == user.Id).ToListAsync()).Single().RoleId;
            return roles.Single(r => r.Id == roleId).Name;
        }
        Assert.Equal("superadmin", await RoleOf("superadmin@acme.com"));
        Assert.Equal("admin", await RoleOf("admin@acme.com"));
        Assert.Equal("employee", await RoleOf("employee@acme.com"));

        // Role ACLs: superadmin is_super_admin; admin gets auth.* + directory.organizations.*; employee empty.
        var acls = await db.Set<RoleAcl>().ToListAsync();
        Assert.Equal(3, acls.Count);

        RoleAcl AclFor(string roleName) => acls.Single(a => a.RoleId == roles.Single(r => r.Name == roleName).Id);

        var superAcl = AclFor("superadmin");
        Assert.True(superAcl.IsSuperAdmin);
        Assert.Equal(new[] { "directory.tenants.*" }, Features(superAcl));

        var adminAcl = AclFor("admin");
        Assert.False(adminAcl.IsSuperAdmin);
        Assert.Equal(
            new[] { "auth.*", "directory.organizations.view", "directory.organizations.manage" },
            Features(adminAcl));

        var employeeAcl = AclFor("employee");
        Assert.False(employeeAcl.IsSuperAdmin);
        Assert.Empty(Features(employeeAcl));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        var registry = Registry();
        await using var db = CreateDb(registry);
        var (hasher, encryption) = Crypto();
        var options = new SetupInitialTenantOptions();

        var first = await InitialTenantSeeder.SetupInitialTenantAsync(db, registry, hasher, encryption, options);
        var second = await InitialTenantSeeder.SetupInitialTenantAsync(db, registry, hasher, encryption, options);

        Assert.False(first.ReusedExistingUser);
        Assert.True(second.ReusedExistingUser);
        Assert.Equal(first.TenantId, second.TenantId);

        // No duplication on the second run.
        Assert.Single(await db.Set<Tenant>().ToListAsync());
        Assert.Single(await db.Set<Organization>().ToListAsync());
        Assert.Equal(3, await db.Set<Role>().CountAsync());
        Assert.Equal(3, await db.Set<User>().CountAsync());
        Assert.Equal(3, await db.Set<UserRole>().CountAsync());
        Assert.Equal(3, await db.Set<RoleAcl>().CountAsync());
    }

    [Fact]
    public async Task Honors_env_overrides_for_derived_users()
    {
        Environment.SetEnvironmentVariable("OM_INIT_ADMIN_EMAIL", "boss@corp.test");
        try
        {
            var registry = Registry();
            await using var db = CreateDb(registry);
            var (hasher, encryption) = Crypto();

            await InitialTenantSeeder.SetupInitialTenantAsync(db, registry, hasher, encryption,
                new SetupInitialTenantOptions { Email = "root@corp.test", OrgSlug = null });

            var users = await db.Set<User>().ToListAsync();
            var emails = users.Select(u => u.Email).OrderBy(e => e).ToArray();
            Assert.Contains("boss@corp.test", emails);
            Assert.Contains("root@corp.test", emails);
            Assert.Contains("employee@corp.test", emails); // derived from primary email domain
        }
        finally
        {
            Environment.SetEnvironmentVariable("OM_INIT_ADMIN_EMAIL", null);
        }
    }

    private static string[] Features(RoleAcl acl) =>
        acl.FeaturesJson is null ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(acl.FeaturesJson)!;
}
