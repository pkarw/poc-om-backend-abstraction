using Microsoft.EntityFrameworkCore;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;
using OpenMercato.Modules.Dashboards.Seeding;
using Xunit;

namespace OpenMercato.Tests.Dashboards;

public class DashboardSeederTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static async Task SeedRoles(Microsoft.EntityFrameworkCore.DbContext db, params string[] names)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var n in names)
            db.Set<Role>().Add(new Role { Id = Guid.NewGuid(), Name = n, TenantId = Tenant, CreatedAt = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SeedDefaults_gives_admin_all_widgets_and_skips_employee()
    {
        using var db = DashboardsTestDb.Create();
        await SeedRoles(db, "admin", "employee");

        var ok = await DashboardSeeder.SeedDefaultsForTenantAsync(db, Tenant, roleNames: new[] { "admin", "employee" });
        Assert.True(ok);

        var records = await db.Set<DashboardRoleWidgets>().ToListAsync();
        var adminRecord = Assert.Single(records); // employee has no defaultEnabled widgets ⇒ no record
        Assert.Equal(10, JsonStrings.ParseArray(adminRecord.WidgetIdsJson).Count);
    }

    [Fact]
    public async Task SeedDefaults_with_explicit_widget_list_filters_to_known_ids()
    {
        using var db = DashboardsTestDb.Create();
        await SeedRoles(db, "admin");

        await DashboardSeeder.SeedDefaultsForTenantAsync(db, Tenant, roleNames: new[] { "admin" },
            widgetIds: new[] { "dashboards.analytics.revenueKpi", "unknown.widget" });

        var record = await db.Set<DashboardRoleWidgets>().SingleAsync();
        Assert.Equal(new[] { "dashboards.analytics.revenueKpi" }, JsonStrings.ParseArray(record.WidgetIdsJson));
    }

    [Fact]
    public async Task AppendWidgetsToRoles_appends_only_missing_ids_to_existing_records()
    {
        using var db = DashboardsTestDb.Create();
        await SeedRoles(db, "employee");
        var role = await db.Set<Role>().SingleAsync();
        db.Set<DashboardRoleWidgets>().Add(new DashboardRoleWidgets
        {
            Id = Guid.NewGuid(), RoleId = role.Id, TenantId = Tenant, OrganizationId = null,
            WidgetIdsJson = JsonStrings.SerializeArray(new[] { "dashboards.analytics.revenueKpi" }),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var changed = await DashboardSeeder.AppendWidgetsToRolesAsync(db, Tenant, null,
            new[] { "employee" }, WidgetCatalog.AnalyticsWidgetIds());
        Assert.True(changed);

        var record = await db.Set<DashboardRoleWidgets>().SingleAsync();
        var ids = JsonStrings.ParseArray(record.WidgetIdsJson);
        Assert.Equal(10, ids.Count);                            // grew to all analytics ids
        Assert.Equal("dashboards.analytics.revenueKpi", ids[0]); // original first (append-only)
    }
}
