using System.Text.Json.Nodes;

namespace OpenMercato.Modules.Dashboards.Services;

/// <summary>A validated widget-data request (parsed from widgetDataRequestSchema).</summary>
public sealed record WidgetDataRequest(
    string EntityType,
    string MetricField,
    string MetricAggregate,
    string? GroupByField,
    string? GroupByGranularity,
    int? GroupByLimit,
    bool? GroupByResolveLabels,
    IReadOnlyList<WidgetDataFilter> Filters,
    string? DateRangeField,
    string? DateRangePreset,
    string? ComparisonType);

/// <summary>A widget-data filter clause.</summary>
public sealed record WidgetDataFilter(string Field, string Operator, JsonNode? Value);

/// <summary>Thrown for request-shape/registry validation failures → surfaced as HTTP 400 with the message.</summary>
public sealed class WidgetDataValidationError : Exception
{
    public WidgetDataValidationError(string message) : base(message) { }
}

/// <summary>
/// Generic aggregation service — the .NET port of upstream <c>services/widgetDataService.ts</c>.
/// Validates a request against the <see cref="IAnalyticsRegistry"/>, then (upstream) builds and
/// executes parameterized SQL, computes comparison periods, and resolves labels.
///
/// PARITY-TODO: the analytics registry is empty in this port (the entity configs live in the
/// unported sales/customers/catalog modules). <see cref="FetchWidgetData"/> therefore always throws
/// <see cref="WidgetDataValidationError"/> "Invalid entity type: &lt;x&gt;" at the validation step —
/// the exact upstream behavior with no registered configs — so the endpoint returns 400 with the
/// right shape rather than 404/500. The SQL build/execute path (buildAggregationQuery) is a
/// PARITY-TODO placeholder that ships the empty response envelope; it is unreachable until a domain
/// module registers an entity config.
/// </summary>
public sealed class WidgetDataService
{
    private static readonly string[] ValidAggregates = { "count", "sum", "avg", "min", "max" };
    private static readonly string[] ValidPresets =
    {
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month",
        "this_quarter", "last_quarter", "this_year", "last_year",
        "last_7_days", "last_30_days", "last_90_days",
    };

    private readonly IAnalyticsRegistry _registry;

    public WidgetDataService(IAnalyticsRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Response envelope for a single widget-data request.</summary>
    public JsonObject FetchWidgetData(WidgetDataRequest request)
    {
        ValidateRequest(request);

        // PARITY-TODO: buildAggregationQuery + raw SQL execution over the entity's table depends on
        // registered field mappings (sales/customers/catalog). Until those modules exist this path is
        // unreachable (validation throws first). Ship the empty envelope shape for forward-compat.
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["value"] = null,
            ["data"] = new JsonArray(),
            ["metadata"] = new JsonObject
            {
                ["fetchedAt"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["recordCount"] = 0,
            },
        };
    }

    private void ValidateRequest(WidgetDataRequest request)
    {
        if (!_registry.IsValidEntityType(request.EntityType))
            throw new WidgetDataValidationError($"Invalid entity type: {request.EntityType}");

        if (string.IsNullOrEmpty(request.MetricField) || string.IsNullOrEmpty(request.MetricAggregate))
            throw new WidgetDataValidationError("Metric field and aggregate are required");

        var metricMapping = _registry.GetFieldMapping(request.EntityType, request.MetricField);
        if (metricMapping is null)
            throw new WidgetDataValidationError(
                $"Invalid metric field: {request.MetricField} for entity type: {request.EntityType}");

        if (!ValidAggregates.Contains(request.MetricAggregate))
            throw new WidgetDataValidationError($"Invalid aggregate function: {request.MetricAggregate}");

        if (request.DateRangePreset is not null && !ValidPresets.Contains(request.DateRangePreset))
            throw new WidgetDataValidationError($"Invalid date range preset: {request.DateRangePreset}");

        if (request.GroupByField is not null)
        {
            var groupMapping = _registry.GetFieldMapping(request.EntityType, request.GroupByField);
            if (groupMapping is null)
            {
                var baseField = request.GroupByField.Split('.')[0];
                var baseMapping = _registry.GetFieldMapping(request.EntityType, baseField);
                if (baseMapping is null || baseMapping.Type != "jsonb")
                    throw new WidgetDataValidationError($"Invalid groupBy field: {request.GroupByField}");
            }
        }
    }
}
