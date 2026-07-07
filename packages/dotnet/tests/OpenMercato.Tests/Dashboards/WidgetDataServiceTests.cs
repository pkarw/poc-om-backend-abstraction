using OpenMercato.Modules.Dashboards.Services;
using Xunit;

namespace OpenMercato.Tests.Dashboards;

public class WidgetDataServiceTests
{
    private static WidgetDataRequest Request(string entityType) => new(
        entityType, "amount", "sum", null, null, null, null,
        Array.Empty<WidgetDataFilter>(), null, null, null);

    [Fact]
    public void Empty_registry_rejects_with_invalid_entity_type()
    {
        var service = new WidgetDataService(new DefaultAnalyticsRegistry());
        var ex = Assert.Throws<WidgetDataValidationError>(() => service.FetchWidgetData(Request("sales:orders")));
        Assert.Equal("Invalid entity type: sales:orders", ex.Message);
    }

    [Fact]
    public void Unknown_metric_field_rejected_when_entity_is_valid()
    {
        // A populated registry whose entity lacks the requested metric field.
        var registry = new DefaultAnalyticsRegistry(new[]
        {
            new AnalyticsModuleConfig(new[]
            {
                new AnalyticsEntityConfig("sales:orders", "sales_orders",
                    new Dictionary<string, AnalyticsFieldMapping>(), RequiredFeatures: null),
            }),
        });
        var service = new WidgetDataService(registry);
        var ex = Assert.Throws<WidgetDataValidationError>(() => service.FetchWidgetData(Request("sales:orders")));
        Assert.Equal("Invalid metric field: amount for entity type: sales:orders", ex.Message);
    }

    [Fact]
    public void Valid_request_returns_the_response_envelope_shape()
    {
        var registry = new DefaultAnalyticsRegistry(new[]
        {
            new AnalyticsModuleConfig(new[]
            {
                new AnalyticsEntityConfig("sales:orders", "sales_orders",
                    new Dictionary<string, AnalyticsFieldMapping>
                    {
                        ["amount"] = new AnalyticsFieldMapping("total_amount", "number"),
                    },
                    RequiredFeatures: new[] { "sales.orders.view" }),
            }),
        });
        var service = new WidgetDataService(registry);
        var result = service.FetchWidgetData(Request("sales:orders"));

        // Envelope: { value: number|null, data: [], metadata: { fetchedAt, recordCount } }.
        Assert.True(result.ContainsKey("value"));
        Assert.Null(result["value"]);
        Assert.NotNull(result["data"]);
        Assert.Empty(result["data"]!.AsArray());
        var metadata = result["metadata"]!.AsObject();
        Assert.True(metadata.ContainsKey("fetchedAt"));
        Assert.Equal(0, metadata["recordCount"]!.GetValue<int>());
    }

    [Fact]
    public void Registry_required_features_surface_per_entity()
    {
        var registry = new DefaultAnalyticsRegistry(new[]
        {
            new AnalyticsModuleConfig(new[]
            {
                new AnalyticsEntityConfig("sales:orders", "sales_orders",
                    new Dictionary<string, AnalyticsFieldMapping>(),
                    RequiredFeatures: new[] { "sales.orders.view" }),
            }),
        });
        Assert.True(registry.IsValidEntityType("sales:orders"));
        Assert.Equal(new[] { "sales.orders.view" }, registry.GetRequiredFeatures("sales:orders"));
        Assert.Null(registry.GetRequiredFeatures("customers:entities"));
    }
}
