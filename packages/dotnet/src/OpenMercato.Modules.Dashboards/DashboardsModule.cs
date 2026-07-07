using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dashboards.Api;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Services;

namespace OpenMercato.Modules.Dashboards;

/// <summary>
/// The dashboards module (upstream packages/core/src/modules/dashboards). Provides the per-user
/// configurable admin dashboard: personal widget layouts, per-role/per-user widget availability, and
/// a generic widget-data aggregation endpoint. Owns three tables (dashboard_layouts,
/// dashboard_role_widgets, dashboard_user_widgets) whose byte-exact DDL is created by the raw-SQL
/// migration AddDashboardsModule; ConfigureModel here only wires the runtime EF model. Depends on
/// auth (RBAC + User/Role/UserRole) and directory (org-scope), both ported. The widget-DATA path
/// additionally depends on sales/customers/catalog (analytics configs) — NOT ported, so the analytics
/// registry is empty and real widget-data requests return 400 "Invalid entity type" (PARITY-TODO).
/// </summary>
public sealed class DashboardsModule : IModule
{
    public string Id => "dashboards";

    /// <summary>The 4 ACL feature ids (acl.ts, all module 'dashboards'). Kept for back-compat.</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "dashboards.view",
        "dashboards.configure",
        "dashboards.admin.assign-widgets",
        "analytics.view",
    };

    /// <summary>The 4 ACL features with their exact upstream titles (acl.ts).</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("dashboards.view", "View dashboard"),
        new AclFeatureDefinition("dashboards.configure", "Customize dashboard layout"),
        new AclFeatureDefinition("dashboards.admin.assign-widgets", "Manage dashboard widget availability"),
        new AclFeatureDefinition("analytics.view", "View analytics widgets"),
    };

    // NotificationTypes: NONE (no notifications.ts).
    // CustomFieldSets: NONE (no ce.ts / data/fields.ts).
    // DeclaredEvents: NONE (index.ts exports only metadata + features; no events.ts).

    /// <summary>Default role features (upstream setup.ts). admin gets dashboards.* (⊇ view+configure);
    /// employee gets view+configure; both get analytics.view so seeded users can load the dashboard.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["admin"] = new[] { "dashboards.*", "dashboards.admin.assign-widgets", "analytics.view" },
            ["employee"] = new[] { "dashboards.view", "dashboards.configure", "analytics.view" },
        };

    /// <summary>CLI subcommands (upstream cli.ts). seed-analytics/debug-analytics are PARITY-TODO
    /// (depend on unported sales/customers/catalog entities) and are intentionally omitted.</summary>
    public IReadOnlyList<ICliCommand> CliCommands { get; } = new ICliCommand[]
    {
        new OpenMercato.Modules.Dashboards.Cli.SeedDefaultsCommand(),
        new OpenMercato.Modules.Dashboards.Cli.EnableAnalyticsWidgetsCommand(),
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // di.ts registers analyticsRegistry as a singleton over getAnalyticsModuleConfigs(). Those
        // configs come from sales/customers/catalog (not ported) → the registry is EMPTY (PARITY-TODO).
        services.AddSingleton<IAnalyticsRegistry>(_ => new DefaultAnalyticsRegistry());
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DashboardLayout>(e =>
        {
            e.ToTable("dashboard_layouts");
            e.HasKey(x => x.Id).HasName("dashboard_layouts_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.LayoutJson).HasColumnName("layout_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.UserId, x.TenantId, x.OrganizationId })
                .IsUnique().HasDatabaseName("dashboard_layouts_user_id_tenant_id_organization_id_unique");
        });

        modelBuilder.Entity<DashboardRoleWidgets>(e =>
        {
            e.ToTable("dashboard_role_widgets");
            e.HasKey(x => x.Id).HasName("dashboard_role_widgets_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.WidgetIdsJson).HasColumnName("widget_ids_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.RoleId, x.TenantId, x.OrganizationId })
                .IsUnique().HasDatabaseName("dashboard_role_widgets_role_id_tenant_id_organization_id_unique");
        });

        modelBuilder.Entity<DashboardUserWidgets>(e =>
        {
            e.ToTable("dashboard_user_widgets");
            e.HasKey(x => x.Id).HasName("dashboard_user_widgets_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Mode).HasColumnName("mode");
            e.Property(x => x.WidgetIdsJson).HasColumnName("widget_ids_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.UserId, x.TenantId, x.OrganizationId })
                .IsUnique().HasDatabaseName("dashboard_user_widgets_user_id_tenant_id_organization_id_unique");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Discover every IDashboardRouteGroup in the Dashboards assembly (parity with Auth/Directory).
        var groupType = typeof(IDashboardRouteGroup);
        var implementations = typeof(DashboardsModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && groupType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in implementations)
            if (Activator.CreateInstance(type) is IDashboardRouteGroup group)
                group.Map(routes);
    }
}
