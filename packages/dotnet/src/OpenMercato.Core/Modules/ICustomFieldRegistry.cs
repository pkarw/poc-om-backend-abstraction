namespace OpenMercato.Core.Modules;

/// <summary>
/// The runtime "supported" surface for custom fields: the declared field sets
/// keyed by entity id. Built from <see cref="ModuleRegistry.AllCustomFieldSets"/>.
///
/// PORT-TODO: the actual EAV value storage/read engine arrives with the entities
/// module port. This registry is only the declared-definition lookup.
/// </summary>
public interface ICustomFieldRegistry
{
    /// <summary>All custom-field sets declared for <paramref name="entityId"/> (e.g. "auth:user"). Empty if none.</summary>
    IReadOnlyList<CustomFieldSet> ForEntity(string entityId);

    /// <summary>All declared custom-field sets across all entities.</summary>
    IReadOnlyList<CustomFieldSet> All { get; }
}

/// <summary>Default in-memory custom-field registry built from the module registry.</summary>
public sealed class CustomFieldRegistry : ICustomFieldRegistry
{
    private readonly IReadOnlyList<CustomFieldSet> _all;
    private readonly ILookup<string, CustomFieldSet> _byEntity;

    public CustomFieldRegistry(ModuleRegistry registry)
    {
        _all = registry.AllCustomFieldSets;
        _byEntity = _all.ToLookup(s => s.EntityId, StringComparer.Ordinal);
    }

    public IReadOnlyList<CustomFieldSet> ForEntity(string entityId) =>
        _byEntity[entityId].ToList();

    public IReadOnlyList<CustomFieldSet> All => _all;
}
