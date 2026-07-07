namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Aggregates the static feature declarations across modules for GET /api/auth/features
/// (upstream api/features.ts). Upstream reads rich per-feature metadata; this port only has the
/// feature-id strings from each module's AclFeatures, so it derives <c>title = id</c> and
/// <c>module = &lt;prefix before the first '.'&gt;</c>. Items are deduped by id (first wins) and sorted
/// by (module, id). No <c>dependsOn</c> is emitted (none is declared in the port).
/// </summary>
public static class FeatureCatalog
{
    public sealed record FeatureItem(string Id, string Title, string Module);
    public sealed record FeatureModule(string Id, string Title);

    public static (List<FeatureItem> Items, List<FeatureModule> Modules) Build(
        IEnumerable<string> featureIds, IEnumerable<(string Id, string Title)> moduleInfos)
    {
        var byId = new Dictionary<string, FeatureItem>(StringComparer.Ordinal);
        foreach (var id in featureIds)
        {
            if (string.IsNullOrEmpty(id) || byId.ContainsKey(id)) continue;
            var dot = id.IndexOf('.');
            var module = dot > 0 ? id[..dot] : id;
            byId[id] = new FeatureItem(id, id, module);
        }

        var items = byId.Values
            .OrderBy(i => i.Module, StringComparer.Ordinal)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();

        var modules = new List<FeatureModule>();
        var seenModules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, title) in moduleInfos)
        {
            if (string.IsNullOrEmpty(id) || !seenModules.Add(id)) continue;
            modules.Add(new FeatureModule(id, string.IsNullOrEmpty(title) ? id : title));
        }

        return (items, modules);
    }
}
