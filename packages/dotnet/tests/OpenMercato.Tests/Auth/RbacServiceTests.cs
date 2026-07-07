using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Services;
using Xunit;

namespace OpenMercato.Tests.Auth;

public class FeatureMatchTests
{
    [Theory]
    [InlineData("users.view", "*", true)]
    [InlineData("entities.records.view", "entities.*", true)]
    [InlineData("entities", "entities.*", true)]           // exact prefix
    [InlineData("users.view", "entities.*", false)]        // different module
    [InlineData("users.view", "users.view", true)]         // exact
    [InlineData("users.view", "users.edit", false)]
    public void Match_wildcards(string required, string granted, bool expected) =>
        Assert.Equal(expected, FeatureMatch.Match(required, granted));

    [Fact]
    public void HasAll_empty_required_is_true() =>
        Assert.True(FeatureMatch.HasAll(Array.Empty<string>(), Array.Empty<string>()));

    [Fact]
    public void HasAll_requires_every_feature()
    {
        Assert.True(FeatureMatch.HasAll(new[] { "a.read", "a.write" }, new[] { "a.*" }));
        Assert.False(FeatureMatch.HasAll(new[] { "a.read", "b.read" }, new[] { "a.*" }));
    }
}

public class RbacServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static async Task<(Guid userId, Guid roleId)> SeedUserWithRole(
        OpenMercato.Core.Data.AppDbContext db)
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        db.Set<User>().Add(new User { Id = userId, TenantId = Tenant, OrganizationId = Org, Email = "e", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<Role>().Add(new Role { Id = roleId, Name = "editor", TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow });
        db.Set<UserRole>().Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (userId, roleId);
    }

    [Fact]
    public async Task Aggregates_role_features_with_wildcard()
    {
        using var db = AuthTestDb.Create();
        var (userId, roleId) = await SeedUserWithRole(db);
        db.Set<RoleAcl>().Add(new RoleAcl { Id = Guid.NewGuid(), RoleId = roleId, TenantId = Tenant, FeaturesJson = "[\"auth.*\"]", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);

        var acl = await rbac.LoadAcl(userId, Tenant, Org);
        Assert.False(acl.IsSuperAdmin);
        Assert.Equal(new[] { "auth.*" }, acl.Features);
        Assert.Null(acl.Organizations); // organizationsJson null => all orgs

        Assert.True(await rbac.UserHasAllFeatures(userId, new[] { "auth.users.list" }, Tenant, Org));
        Assert.False(await rbac.UserHasAllFeatures(userId, new[] { "billing.view" }, Tenant, Org));
    }

    [Fact]
    public async Task Per_user_acl_wins_exclusively_over_roles()
    {
        using var db = AuthTestDb.Create();
        var (userId, roleId) = await SeedUserWithRole(db);
        // Role grants y.* but the per-user ACL grants only x.read — the user ACL must win exclusively.
        db.Set<RoleAcl>().Add(new RoleAcl { Id = Guid.NewGuid(), RoleId = roleId, TenantId = Tenant, FeaturesJson = "[\"y.manage\"]", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<UserAcl>().Add(new UserAcl { Id = Guid.NewGuid(), UserId = userId, TenantId = Tenant, FeaturesJson = "[\"x.read\"]", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);

        var acl = await rbac.LoadAcl(userId, Tenant, Org);
        Assert.Equal(new[] { "x.read" }, acl.Features);
        Assert.False(await rbac.UserHasAllFeatures(userId, new[] { "y.manage" }, Tenant, Org));
        Assert.True(await rbac.UserHasAllFeatures(userId, new[] { "x.read" }, Tenant, Org));
    }

    [Fact]
    public async Task Super_admin_role_grants_everything()
    {
        using var db = AuthTestDb.Create();
        var (userId, roleId) = await SeedUserWithRole(db);
        db.Set<RoleAcl>().Add(new RoleAcl { Id = Guid.NewGuid(), RoleId = roleId, TenantId = Tenant, IsSuperAdmin = true, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);

        var acl = await rbac.LoadAcl(userId, Tenant, Org);
        Assert.True(acl.IsSuperAdmin);
        Assert.Equal(new[] { "*" }, acl.Features);
        Assert.Null(acl.Organizations);
        Assert.True(await rbac.UserHasAllFeatures(userId, new[] { "anything.at.all" }, Tenant, Org));
    }

    [Fact]
    public async Task Org_restriction_blocks_out_of_scope_org()
    {
        using var db = AuthTestDb.Create();
        var (userId, roleId) = await SeedUserWithRole(db);
        var allowedOrg = Org.ToString();
        db.Set<RoleAcl>().Add(new RoleAcl
        {
            Id = Guid.NewGuid(), RoleId = roleId, TenantId = Tenant,
            FeaturesJson = "[\"auth.users.list\"]",
            OrganizationsJson = $"[\"{allowedOrg}\"]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);

        Assert.True(await rbac.UserHasAllFeatures(userId, new[] { "auth.users.list" }, Tenant, Org));
        Assert.False(await rbac.UserHasAllFeatures(userId, new[] { "auth.users.list" }, Tenant, Guid.NewGuid()));
    }

    [Fact]
    public async Task No_features_required_is_always_true()
    {
        using var db = AuthTestDb.Create();
        var (userId, _) = await SeedUserWithRole(db);
        var rbac = new RbacService(db);
        Assert.True(await rbac.UserHasAllFeatures(userId, Array.Empty<string>(), Tenant, Org));
    }
}
