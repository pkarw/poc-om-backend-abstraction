using System.Text.Json.Nodes;

namespace OpenMercato.Modules.Dashboards.Lib;

/// <summary>
/// Server-side metadata for one dashboard widget — the load-bearing subset of upstream
/// <c>DashboardWidgetMetadata</c> surfaced by the layout/catalog APIs. The React widget body
/// (<c>widget.client.tsx</c>) is out of scope; only this metadata drives the backend responses.
/// <see cref="LoaderKey"/> is the client-registry key with no backend meaning — the port emits the
/// exact upstream synthetic key (<c>&lt;moduleId&gt;:&lt;dir&gt;:widget</c>).
/// </summary>
public sealed record DashboardWidget(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<string> Features,
    string DefaultSize,
    bool DefaultEnabled,
    JsonObject? DefaultSettings,
    IReadOnlyList<string> Tags,
    string Category,
    string? Icon,
    bool SupportsRefresh,
    string ModuleId,
    string LoaderKey);

/// <summary>
/// The server-side widget catalog — the .NET analogue of upstream <c>lib/widgets.ts</c>
/// <c>loadAllWidgets()</c>. Ships the 10 built-in analytics widgets contributed by the dashboards
/// module (all <c>defaultEnabled: false</c>, so a first-time user's default layout is empty). Other
/// modules would contribute additional widgets through the same registry; until they are ported the
/// catalog is exactly these 10 (deduped by id, first wins — parity with upstream).
/// </summary>
public static class WidgetCatalog
{
    private const string ModuleId = "dashboards";

    private static JsonObject Settings(params (string Key, JsonNode? Value)[] pairs)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in pairs) obj[k] = v;
        return obj;
    }

    // The 10 built-in analytics widgets (upstream widgets/dashboard/<dir>/widget.ts metadata +
    // config.ts DEFAULT_SETTINGS). category 'analytics', defaultEnabled false, supportsRefresh true.
    private static readonly IReadOnlyList<DashboardWidget> BuiltIn = new List<DashboardWidget>
    {
        new("dashboards.analytics.revenueKpi", "Revenue", "Total revenue with period comparison",
            new[] { "analytics.view", "sales.orders.view" }, "sm", false,
            Settings(("dateRange", "this_month"), ("showComparison", true)),
            new[] { "analytics", "sales", "kpi" }, "analytics", "dollar-sign", true,
            ModuleId, "dashboards:revenue-kpi:widget"),

        new("dashboards.analytics.ordersKpi", "Orders", "Total order count with period comparison",
            new[] { "analytics.view", "sales.orders.view" }, "sm", false,
            Settings(("dateRange", "this_month"), ("showComparison", true)),
            new[] { "analytics", "sales", "kpi" }, "analytics", "shopping-cart", true,
            ModuleId, "dashboards:orders-kpi:widget"),

        new("dashboards.analytics.aovKpi", "Average Order Value", "Average order value with period comparison",
            new[] { "analytics.view", "sales.orders.view" }, "sm", false,
            Settings(("dateRange", "this_month"), ("showComparison", true)),
            new[] { "analytics", "sales", "kpi" }, "analytics", "trending-up", true,
            ModuleId, "dashboards:aov-kpi:widget"),

        new("dashboards.analytics.newCustomersKpi", "Customer Growth", "New customer count with period comparison",
            new[] { "analytics.view", "customers.people.view" }, "sm", false,
            Settings(("dateRange", "this_month"), ("showComparison", true)),
            new[] { "analytics", "customers", "kpi" }, "analytics", "user-plus", true,
            ModuleId, "dashboards:new-customers-kpi:widget"),

        new("dashboards.analytics.ordersByStatus", "Orders by Status", "Distribution of orders by status",
            new[] { "analytics.view", "sales.orders.view" }, "sm", false,
            Settings(("dateRange", "this_month"), ("variant", "donut")),
            new[] { "analytics", "sales", "chart" }, "analytics", "pie-chart", true,
            ModuleId, "dashboards:orders-by-status:widget"),

        new("dashboards.analytics.revenueTrend", "Revenue Trend", "Revenue over time with customizable granularity",
            new[] { "analytics.view", "sales.orders.view" }, "lg", false,
            Settings(("dateRange", "last_30_days"), ("granularity", "day"), ("showArea", true)),
            new[] { "analytics", "sales", "chart" }, "analytics", "line-chart", true,
            ModuleId, "dashboards:revenue-trend:widget"),

        new("dashboards.analytics.salesByRegion", "Sales by Region", "Revenue distribution by shipping region",
            new[] { "analytics.view", "sales.orders.view" }, "md", false,
            Settings(("dateRange", "this_month"), ("limit", 10)),
            new[] { "analytics", "sales", "geography", "chart" }, "analytics", "map-pin", true,
            ModuleId, "dashboards:sales-by-region:widget"),

        new("dashboards.analytics.pipelineSummary", "Pipeline Summary", "Deal value by pipeline stage",
            new[] { "analytics.view", "customers.deals.view" }, "md", false,
            Settings(("dateRange", "this_month")),
            new[] { "analytics", "customers", "deals", "chart" }, "analytics", "git-branch", true,
            ModuleId, "dashboards:pipeline-summary:widget"),

        new("dashboards.analytics.topCustomers", "Top Customers", "Top customers by revenue",
            new[] { "analytics.view", "sales.orders.view", "customers.people.view" }, "md", false,
            Settings(("dateRange", "this_month"), ("limit", 10)),
            new[] { "analytics", "sales", "customers", "table" }, "analytics", "users", true,
            ModuleId, "dashboards:top-customers:widget"),

        new("dashboards.analytics.topProducts", "Top Products", "Top-selling products by revenue",
            new[] { "analytics.view", "sales.orders.view" }, "md", false,
            Settings(("dateRange", "this_month"), ("limit", 10), ("layout", "horizontal")),
            new[] { "analytics", "sales", "products", "chart" }, "analytics", "bar-chart-2", true,
            ModuleId, "dashboards:top-products:widget"),
    };

    /// <summary>Loaded widget list, deduped by id (first wins). Parity with loadAllWidgets().</summary>
    public static IReadOnlyList<DashboardWidget> LoadAll()
    {
        var byId = new Dictionary<string, DashboardWidget>();
        foreach (var w in BuiltIn)
            if (!byId.ContainsKey(w.Id)) byId[w.Id] = w;
        return byId.Values.ToList();
    }

    /// <summary>All known widget ids.</summary>
    public static IReadOnlyList<string> AllIds() => LoadAll().Select(w => w.Id).ToList();

    /// <summary>Analytics widget ids (category 'analytics' OR id under 'dashboards.analytics.').
    /// Parity with resolveAnalyticsWidgetIds().</summary>
    public static IReadOnlyList<string> AnalyticsWidgetIds() => LoadAll()
        .Where(w => w.Category == "analytics" || w.Id.StartsWith("dashboards.analytics.", StringComparison.Ordinal))
        .Select(w => w.Id).ToList();

    /// <summary>Widget ids whose defaultEnabled is true (currently none).</summary>
    public static IReadOnlyList<string> DefaultEnabledIds() =>
        LoadAll().Where(w => w.DefaultEnabled).Select(w => w.Id).ToList();

    /// <summary>Serialize a widget to the API summary object (layout GET widgets[] / catalog items[]).</summary>
    public static JsonObject ToSummary(DashboardWidget w)
    {
        var features = new JsonArray();
        foreach (var f in w.Features) features.Add(f);
        return new JsonObject
        {
            ["id"] = w.Id,
            ["title"] = w.Title,
            ["description"] = w.Description is null ? null : w.Description,
            ["defaultSize"] = w.DefaultSize,
            ["defaultEnabled"] = w.DefaultEnabled,
            ["defaultSettings"] = w.DefaultSettings?.DeepClone(),
            ["features"] = features,
            ["moduleId"] = w.ModuleId,
            ["icon"] = w.Icon is null ? null : w.Icon,
            ["loaderKey"] = w.LoaderKey,
            ["supportsRefresh"] = w.SupportsRefresh,
        };
    }
}
