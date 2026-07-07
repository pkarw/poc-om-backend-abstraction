using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dashboards;
using OpenMercato.Modules.Dashboards.Lib;
using Xunit;

namespace OpenMercato.Tests.Dashboards;

public class WidgetCatalogTests
{
    [Fact]
    public void Ships_ten_builtin_analytics_widgets_all_default_disabled()
    {
        var widgets = WidgetCatalog.LoadAll();
        Assert.Equal(10, widgets.Count);
        Assert.All(widgets, w =>
        {
            Assert.False(w.DefaultEnabled);
            Assert.True(w.SupportsRefresh);
            Assert.Equal("analytics", w.Category);
            Assert.Equal("dashboards", w.ModuleId);
            Assert.StartsWith("dashboards.analytics.", w.Id);
        });
        // All default-enabled ⇒ default layout is empty (contract DEFAULT-LAYOUT NOTE).
        Assert.Empty(WidgetCatalog.DefaultEnabledIds());
        Assert.Equal(10, WidgetCatalog.AnalyticsWidgetIds().Count);
    }

    [Fact]
    public void Revenue_kpi_metadata_is_load_bearing()
    {
        var w = WidgetCatalog.LoadAll().Single(x => x.Id == "dashboards.analytics.revenueKpi");
        Assert.Equal("Revenue", w.Title);
        Assert.Equal("sm", w.DefaultSize);
        Assert.Equal("dollar-sign", w.Icon);
        Assert.Equal(new[] { "analytics.view", "sales.orders.view" }, w.Features);
        Assert.Equal("dashboards:revenue-kpi:widget", w.LoaderKey);
        Assert.NotNull(w.DefaultSettings);
        Assert.Equal("this_month", w.DefaultSettings!["dateRange"]!.GetValue<string>());
        Assert.True(w.DefaultSettings!["showComparison"]!.GetValue<bool>());
    }

    [Fact]
    public void Summary_shape_matches_layout_widgets_envelope()
    {
        var w = WidgetCatalog.LoadAll().First();
        var json = WidgetCatalog.ToSummary(w).ToJsonString();
        foreach (var key in new[] { "id", "title", "description", "defaultSize", "defaultEnabled",
                     "defaultSettings", "features", "moduleId", "icon", "loaderKey", "supportsRefresh" })
            Assert.Contains($"\"{key}\"", json);
    }

    [Fact]
    public void Module_declares_expected_surface()
    {
        IModule m = new DashboardsModule();
        Assert.Equal("dashboards", m.Id);
        Assert.Equal(4, m.AclFeatureDefinitions.Count);
        Assert.Equal("View dashboard", m.AclFeatureDefinitions.Single(f => f.Id == "dashboards.view").Title);
        Assert.Empty(m.DeclaredEvents);
        Assert.Empty(m.NotificationTypes);
        Assert.Empty(m.CustomFieldSets);
        Assert.Equal(2, m.CliCommands.Count);
        Assert.Contains("dashboards.*", m.DefaultRoleFeatures["admin"]);
        Assert.Contains("dashboards.view", m.DefaultRoleFeatures["employee"]);
    }
}
