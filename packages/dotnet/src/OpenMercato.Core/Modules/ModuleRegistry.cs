using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OpenMercato.Core.Modules;

/// <summary>
/// Ordered collection of registered modules. Upstream discovers modules from the
/// filesystem at build time; here registration is explicit in each host's
/// composition root (see OpenMercato.Api/ModuleCatalog.cs).
/// </summary>
public sealed class ModuleRegistry
{
    public ModuleRegistry(IEnumerable<IModule> modules)
    {
        Modules = modules.ToList();
        var duplicate = Modules.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate module id registered: '{duplicate.Key}'.");
    }

    public IReadOnlyList<IModule> Modules { get; }

    /// <summary>
    /// Stable signature of the registered module set (ordered ids). Used as part of the EF model
    /// cache key so distinct registries (e.g. per-test) never collide on the shared AppDbContext's
    /// globally-cached model (which is otherwise keyed by context CLR type alone).
    /// </summary>
    public string ModelCacheKey => string.Join(",", Modules.Select(m => m.Id));

    public IReadOnlyList<string> AclFeatures =>
        Modules.SelectMany(m => m.AclFeatures).Distinct().ToList();

    /// <summary>
    /// Richer RBAC feature declarations flattened across modules (upstream acl.ts titles).
    /// Deduped by id (first module wins), so overlapping bare/rich declarations collapse cleanly.
    /// </summary>
    public IReadOnlyList<AclFeatureDefinition> AllAclFeatureDefinitions =>
        Modules.SelectMany(m => m.AclFeatureDefinitions)
            .GroupBy(f => f.Id)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Notification types flattened across modules (upstream notifications.ts).
    /// Throws on a duplicate <see cref="NotificationTypeDefinition.Type"/> across modules,
    /// mirroring the duplicate module-id guard.
    /// </summary>
    public IReadOnlyList<NotificationTypeDefinition> AllNotificationTypes =>
        FlattenUnique(
            m => m.NotificationTypes,
            n => n.Type,
            "notification type");

    /// <summary>
    /// Declared events flattened across modules (upstream events.ts).
    /// Throws on a duplicate <see cref="EventDeclaration.Name"/> across modules.
    /// </summary>
    public IReadOnlyList<EventDeclaration> AllDeclaredEvents =>
        FlattenUnique(
            m => m.DeclaredEvents,
            e => e.Name,
            "event");

    /// <summary>Custom-field sets flattened across modules (upstream ce.ts / data/fields.ts).</summary>
    public IReadOnlyList<CustomFieldSet> AllCustomFieldSets =>
        Modules.SelectMany(m => m.CustomFieldSets).ToList();

    /// <summary>
    /// Per-role default features merged across every module (upstream ensureDefaultRoleAcls: it
    /// concatenates each module's setup.defaultRoleFeatures per role). Preserves module registration
    /// order and dedupes features per role (parity with the Set-based merge upstream).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MergedDefaultRoleFeatures
    {
        get
        {
            var merged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var module in Modules)
            {
                foreach (var (role, features) in module.DefaultRoleFeatures)
                {
                    if (!merged.TryGetValue(role, out var list))
                    {
                        list = new List<string>();
                        merged[role] = list;
                    }
                    foreach (var feature in features)
                        if (!list.Contains(feature))
                            list.Add(feature);
                }
            }
            return merged.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Default encryption maps aggregated across every module (upstream
    /// <c>getDefaultEncryptionMaps</c>). Deduped by <see cref="ModuleEncryptionMap.EntityId"/>
    /// (first module wins), guarding against a duplicate entity id declaration.
    /// </summary>
    public IReadOnlyList<ModuleEncryptionMap> MergedDefaultEncryptionMaps
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ModuleEncryptionMap>();
            foreach (var module in Modules)
                foreach (var map in module.DefaultEncryptionMaps)
                    if (seen.Add(map.EntityId))
                        result.Add(map);
            return result;
        }
    }

    /// <summary>
    /// CLR entity type → upstream <c>entity_id</c> map aggregated across every module. Consumed by
    /// the SaveChanges encryption interceptor. Throws on a duplicate CLR type across modules.
    /// </summary>
    public IReadOnlyDictionary<Type, string> EncryptedEntityTypeMap
    {
        get
        {
            var map = new Dictionary<Type, string>();
            foreach (var module in Modules)
                foreach (var (type, entityId) in module.EncryptedEntityTypes)
                {
                    if (map.ContainsKey(type))
                        throw new InvalidOperationException(
                            $"Duplicate encrypted entity type declared: '{type.FullName}' (module '{module.Id}').");
                    map[type] = entityId;
                }
            return map;
        }
    }

    private List<T> FlattenUnique<T>(
        Func<IModule, IEnumerable<T>> select,
        Func<T, string> keyOf,
        string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<T>();
        foreach (var module in Modules)
        {
            foreach (var item in select(module))
            {
                var key = keyOf(item);
                if (!seen.Add(key))
                    throw new InvalidOperationException(
                        $"Duplicate {label} declared: '{key}' (module '{module.Id}').");
                result.Add(item);
            }
        }
        return result;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var module in Modules) module.ConfigureServices(services);
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        foreach (var module in Modules) module.ConfigureModel(modelBuilder);
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        foreach (var module in Modules) module.MapRoutes(routes);
    }
}
