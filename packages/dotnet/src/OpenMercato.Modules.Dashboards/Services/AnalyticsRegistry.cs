namespace OpenMercato.Modules.Dashboards.Services;

/// <summary>An analytics field mapping (column + type) — the subset the aggregation builder needs.</summary>
public sealed record AnalyticsFieldMapping(string Column, string Type);

/// <summary>An analytics entity config contributed by a domain module (upstream AnalyticsEntityConfig).</summary>
public sealed record AnalyticsEntityConfig(
    string EntityId,
    string Table,
    IReadOnlyDictionary<string, AnalyticsFieldMapping> FieldMappings,
    IReadOnlyList<string>? RequiredFeatures);

/// <summary>An analytics module config — one module's contributed entity configs.</summary>
public sealed record AnalyticsModuleConfig(IReadOnlyList<AnalyticsEntityConfig> Entities);

/// <summary>
/// The analytics registry — a 1:1 port of upstream <c>services/analyticsRegistry.ts</c>. Maps an
/// analytics entity id to its config (field mappings + required features). Configs are contributed
/// by the <c>sales</c>/<c>customers</c>/<c>catalog</c> modules.
///
/// PARITY-TODO: those modules are not ported, so the registry is EMPTY. Consequently
/// <see cref="IsValidEntityType"/> is always false and every real widget-data request rejects with
/// <c>Invalid entity type: &lt;x&gt;</c> — the exact upstream behavior when no analytics configs are
/// registered. The registry mechanism is in place for those modules to populate later.
/// </summary>
public interface IAnalyticsRegistry
{
    IReadOnlyList<AnalyticsEntityConfig> GetAllEntityConfigs();
    AnalyticsEntityConfig? GetEntityConfig(string entityId);
    bool IsValidEntityType(string entityId);
    AnalyticsFieldMapping? GetFieldMapping(string entityId, string field);
    IReadOnlyList<string>? GetRequiredFeatures(string entityId);
}

/// <inheritdoc />
public sealed class DefaultAnalyticsRegistry : IAnalyticsRegistry
{
    private readonly Dictionary<string, AnalyticsEntityConfig> _entityConfigMap = new();

    public DefaultAnalyticsRegistry(IEnumerable<AnalyticsModuleConfig>? configs = null)
    {
        foreach (var module in configs ?? Array.Empty<AnalyticsModuleConfig>())
            foreach (var entity in module.Entities)
                _entityConfigMap[entity.EntityId] = entity;
    }

    public IReadOnlyList<AnalyticsEntityConfig> GetAllEntityConfigs() => _entityConfigMap.Values.ToList();

    public AnalyticsEntityConfig? GetEntityConfig(string entityId) =>
        _entityConfigMap.TryGetValue(entityId, out var config) ? config : null;

    public bool IsValidEntityType(string entityId) => _entityConfigMap.ContainsKey(entityId);

    public AnalyticsFieldMapping? GetFieldMapping(string entityId, string field) =>
        GetEntityConfig(entityId) is { } c && c.FieldMappings.TryGetValue(field, out var m) ? m : null;

    public IReadOnlyList<string>? GetRequiredFeatures(string entityId) => GetEntityConfig(entityId)?.RequiredFeatures;
}
