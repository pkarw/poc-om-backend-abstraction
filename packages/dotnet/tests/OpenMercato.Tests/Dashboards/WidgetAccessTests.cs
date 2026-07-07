using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;
using Xunit;

namespace OpenMercato.Tests.Dashboards;

public class WidgetAccessTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly string[] AllFeatures =
    {
        "analytics.view", "sales.orders.view", "customers.people.view", "customers.deals.view",
    };

    [Fact]
    public async Task Superadmin_with_no_config_sees_all_widgets()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        var ctx = new WidgetAccessContext(userId, Tenant, null, Array.Empty<string>(), IsSuperAdmin: true);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db, ctx, WidgetCatalog.LoadAll());
        Assert.Equal(10, allowed.Count);
    }

    [Fact]
    public async Task Feature_filter_hides_widgets_the_user_cannot_satisfy()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        // Only analytics.view — every widget also needs a sales/customers feature ⇒ none pass.
        var ctx = new WidgetAccessContext(userId, Tenant, null, new[] { "analytics.view" }, IsSuperAdmin: false);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db, ctx, WidgetCatalog.LoadAll());
        Assert.Empty(allowed);
    }

    [Fact]
    public async Task Role_config_narrows_to_its_list()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<Role>().Add(new Role { Id = roleId, Name = "admin", TenantId = Tenant, CreatedAt = now });
        db.Set<UserRole>().Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, CreatedAt = now });
        db.Set<DashboardRoleWidgets>().Add(new DashboardRoleWidgets
        {
            Id = Guid.NewGuid(), RoleId = roleId, TenantId = Tenant, OrganizationId = null,
            WidgetIdsJson = JsonStrings.SerializeArray(new[] { "dashboards.analytics.revenueKpi" }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var ctx = new WidgetAccessContext(userId, Tenant, null, AllFeatures, IsSuperAdmin: false);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db, ctx, WidgetCatalog.LoadAll());
        Assert.Equal(new[] { "dashboards.analytics.revenueKpi" }, allowed);
    }

    [Fact]
    public async Task Empty_user_override_hides_everything()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<DashboardUserWidgets>().Add(new DashboardUserWidgets
        {
            Id = Guid.NewGuid(), UserId = userId, TenantId = Tenant, OrganizationId = null,
            Mode = "override", WidgetIdsJson = "[]", CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var ctx = new WidgetAccessContext(userId, Tenant, null, AllFeatures, IsSuperAdmin: true);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db, ctx, WidgetCatalog.LoadAll());
        Assert.Empty(allowed);
    }

    [Fact]
    public async Task Tenant_isolation_ignores_other_tenants_role_records()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<Role>().Add(new Role { Id = roleId, Name = "admin", TenantId = Tenant, CreatedAt = now });
        db.Set<UserRole>().Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, CreatedAt = now });
        // A record scoped to a DIFFERENT tenant must be skipped ⇒ falls back to "all".
        db.Set<DashboardRoleWidgets>().Add(new DashboardRoleWidgets
        {
            Id = Guid.NewGuid(), RoleId = roleId, TenantId = otherTenant, OrganizationId = null,
            WidgetIdsJson = JsonStrings.SerializeArray(new[] { "dashboards.analytics.revenueKpi" }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var ctx = new WidgetAccessContext(userId, Tenant, null, AllFeatures, IsSuperAdmin: true);
        var allowed = await WidgetAccess.ResolveAllowedWidgetIds(db, ctx, WidgetCatalog.LoadAll());
        Assert.Equal(10, allowed.Count); // conflicting record skipped ⇒ base set = all
    }
}
