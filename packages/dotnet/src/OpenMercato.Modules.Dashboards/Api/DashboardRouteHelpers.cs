using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenMercato.Modules.Dashboards.Services;

namespace OpenMercato.Modules.Dashboards.Api;

/// <summary>Outcome of reading a JSON request body: whether it parsed, and the parsed node.</summary>
internal readonly record struct JsonBody(bool Parsed, JsonNode? Node)
{
    public JsonObject? AsObject => Node as JsonObject;
}

/// <summary>Shared helpers for the dashboards route groups: JSON body reading, primitive coercion,
/// and parsing/validation of the widget-data request schema.</summary>
internal static class DashboardRouteHelpers
{
    /// <summary>Read + parse the request body as JSON. <c>Parsed=false</c> ⇒ 400 "Invalid JSON body".</summary>
    public static async Task<JsonBody> ReadJsonAsync(HttpContext http)
    {
        try
        {
            using var reader = new StreamReader(http.Request.Body);
            var text = await reader.ReadToEndAsync();
            var node = JsonNode.Parse(text);
            return new JsonBody(true, node);
        }
        catch { return new JsonBody(false, null); }
    }

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static int? Int(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d))
            {
                if (d != Math.Floor(d)) return null;
                return (int)d;
            }
        }
        return null;
    }

    public static Guid? Guid(string? raw) =>
        !string.IsNullOrEmpty(raw) && System.Guid.TryParse(raw, out var g) ? g : null;

    /// <summary>
    /// Parse + validate a body object against widgetDataRequestSchema. Returns null (with
    /// <paramref name="issues"/> populated) on validation failure → 400 "Invalid request payload".
    /// </summary>
    public static WidgetDataRequest? ParseWidgetDataRequest(JsonObject? body, out JsonArray issues)
    {
        issues = new JsonArray();
        if (body is null) { AddIssue(issues, "", "Expected object"); return null; }

        var entityType = Str(body["entityType"]);
        if (string.IsNullOrEmpty(entityType)) AddIssue(issues, "entityType", "Required, min length 1");

        var metric = body["metric"] as JsonObject;
        var metricField = Str(metric?["field"]);
        var metricAggregate = Str(metric?["aggregate"]);
        if (metric is null) AddIssue(issues, "metric", "Required");
        else
        {
            if (string.IsNullOrEmpty(metricField)) AddIssue(issues, "metric.field", "Required, min length 1");
            if (string.IsNullOrEmpty(metricAggregate)) AddIssue(issues, "metric.aggregate", "Required");
        }

        string? groupByField = null, groupByGranularity = null;
        int? groupByLimit = null; bool? groupByResolveLabels = null;
        if (body["groupBy"] is JsonObject gb)
        {
            groupByField = Str(gb["field"]);
            if (string.IsNullOrEmpty(groupByField)) AddIssue(issues, "groupBy.field", "Required, min length 1");
            groupByGranularity = Str(gb["granularity"]);
            groupByLimit = Int(gb["limit"]);
            if (gb["limit"] is not null && (groupByLimit is null || groupByLimit < 1 || groupByLimit > 100))
                AddIssue(issues, "groupBy.limit", "Must be an int 1..100");
            groupByResolveLabels = Bool(gb["resolveLabels"]);
        }

        var filters = new List<WidgetDataFilter>();
        if (body["filters"] is JsonArray fa)
        {
            foreach (var f in fa)
            {
                if (f is not JsonObject fo) { AddIssue(issues, "filters", "Each filter must be an object"); continue; }
                var field = Str(fo["field"]);
                var op = Str(fo["operator"]);
                if (string.IsNullOrEmpty(field)) AddIssue(issues, "filters.field", "Required, min length 1");
                if (string.IsNullOrEmpty(op) || !FilterOperators.Contains(op)) AddIssue(issues, "filters.operator", "Invalid operator");
                if (field is not null && op is not null && FilterOperators.Contains(op))
                    filters.Add(new WidgetDataFilter(field, op, fo["value"]?.DeepClone()));
            }
        }

        string? dateRangeField = null, dateRangePreset = null;
        if (body["dateRange"] is JsonObject dr)
        {
            dateRangeField = Str(dr["field"]);
            dateRangePreset = Str(dr["preset"]);
            if (string.IsNullOrEmpty(dateRangeField)) AddIssue(issues, "dateRange.field", "Required, min length 1");
            if (string.IsNullOrEmpty(dateRangePreset) || !DateRangePresets.Contains(dateRangePreset))
                AddIssue(issues, "dateRange.preset", "Invalid preset");
        }

        string? comparisonType = null;
        if (body["comparison"] is JsonObject cmp)
        {
            comparisonType = Str(cmp["type"]);
            if (comparisonType is not ("previous_period" or "previous_year"))
                AddIssue(issues, "comparison.type", "Invalid comparison type");
        }

        if (issues.Count > 0) return null;

        return new WidgetDataRequest(
            entityType!, metricField!, metricAggregate!,
            groupByField, groupByGranularity, groupByLimit, groupByResolveLabels,
            filters, dateRangeField, dateRangePreset, comparisonType);
    }

    private static readonly HashSet<string> FilterOperators = new(StringComparer.Ordinal)
    { "eq", "neq", "gt", "gte", "lt", "lte", "in", "not_in", "is_null", "is_not_null" };

    private static readonly HashSet<string> DateRangePresets = new(StringComparer.Ordinal)
    {
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month",
        "this_quarter", "last_quarter", "this_year", "last_year",
        "last_7_days", "last_30_days", "last_90_days",
    };

    private static void AddIssue(JsonArray issues, string path, string message) =>
        issues.Add(new JsonObject { ["path"] = path, ["message"] = message });

    public static IResult JsonContent(JsonNode node, int statusCode = 200) =>
        Results.Content(node.ToJsonString(), "application/json", statusCode: statusCode);
}
