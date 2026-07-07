using System.Text.Json.Nodes;
using OpenMercato.Modules.Dashboards.Data;
using OpenMercato.Modules.Dashboards.Lib;
using Xunit;

namespace OpenMercato.Tests.Dashboards;

public class DashboardLayoutTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Fresh_user_default_layout_is_empty()
    {
        // Default items = allowed widgets with defaultEnabled=true; all 10 ship false ⇒ empty.
        var allowed = WidgetCatalog.LoadAll();
        var defaults = allowed.Where(w => w.DefaultEnabled).ToList();
        Assert.Empty(defaults);
    }

    [Fact]
    public void Normalize_dedupes_sorts_and_reindexes_densely()
    {
        var arr = new JsonArray
        {
            new JsonObject { ["id"] = "b", ["widgetId"] = "w2", ["order"] = 5 },
            new JsonObject { ["id"] = "a", ["widgetId"] = "w1", ["order"] = 2 },
            new JsonObject { ["id"] = "a", ["widgetId"] = "w1-dup", ["order"] = 9 }, // duplicate id dropped
            new JsonObject { ["id"] = "", ["widgetId"] = "w3" },                      // empty id dropped
        };
        var items = LayoutJson.Normalize(arr);
        Assert.Equal(2, items.Count);
        Assert.Equal("a", LayoutJson.ItemId(items[0]));          // order 2 sorts first
        Assert.Equal("b", LayoutJson.ItemId(items[1]));
        Assert.Equal(0, items[0]["order"]!.GetValue<int>());
        Assert.Equal(0, items[0]["priority"]!.GetValue<int>());
        Assert.Equal(1, items[1]["order"]!.GetValue<int>());
        Assert.Equal(1, items[1]["priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task Persistence_round_trip_preserves_items_and_settings()
    {
        using var db = DashboardsTestDb.Create();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Simulate a PUT-sanitized layout: dense order/priority, a size and arbitrary settings.
        var items = new List<JsonObject>
        {
            new()
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["widgetId"] = "dashboards.analytics.revenueKpi",
                ["order"] = 0, ["priority"] = 0, ["size"] = "sm",
                ["settings"] = new JsonObject { ["dateRange"] = "this_month" },
            },
        };
        db.Set<DashboardLayout>().Add(new DashboardLayout
        {
            Id = Guid.NewGuid(), UserId = userId, TenantId = Tenant, OrganizationId = null,
            LayoutJson = LayoutJson.Serialize(items), CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // Reload + normalize (the GET path).
        db.ChangeTracker.Clear();
        var reloaded = db.Set<DashboardLayout>().Single(l => l.UserId == userId);
        var normalized = LayoutJson.Normalize(LayoutJson.Parse(reloaded.LayoutJson));

        Assert.Single(normalized);
        Assert.Equal("dashboards.analytics.revenueKpi", LayoutJson.WidgetId(normalized[0]));
        Assert.Equal("sm", normalized[0]["size"]!.GetValue<string>());
        Assert.Equal("this_month", normalized[0]["settings"]!["dateRange"]!.GetValue<string>());
        Assert.Equal(0, normalized[0]["order"]!.GetValue<int>());
    }
}
