using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Directory.Lib;

/// <summary>Minimal input for the pure hierarchy computation (id/parentId as strings, mirroring TS).</summary>
public sealed record OrgHierarchyInput(string Id, string? ParentId, string Name, bool IsActive);

/// <summary>A computed organization tree node — 1:1 with upstream ComputedOrganizationNode.</summary>
public sealed record ComputedOrganizationNode(
    string Id,
    string TenantId,
    string Name,
    string PathLabel,
    string? ParentId,
    int Depth,
    string RootId,
    string TreePath,
    IReadOnlyList<string> AncestorIds,
    IReadOnlyList<string> ChildIds,
    IReadOnlyList<string> DescendantIds,
    bool IsActive);

/// <summary>Result of the hierarchy computation: id→node map plus insertion-ordered node list.</summary>
public sealed record ComputedHierarchy(
    IReadOnlyDictionary<string, ComputedOrganizationNode> Map,
    IReadOnlyList<ComputedOrganizationNode> Ordered);

/// <summary>
/// 1:1 port of upstream packages/core/src/modules/directory/lib/hierarchy.ts. Pure tree builder
/// (<see cref="Compute"/>) plus the EF-backed <see cref="RebuildForTenantAsync"/> that reproduces
/// rebuildHierarchyForTenant, and <see cref="ResolveUniqueSlugAsync"/>. Reused by the CRUD command
/// handlers, the seeder, and auth's org-scope resolution.
/// </summary>
public static class OrganizationHierarchy
{
    /// <summary>normalizeUuid: empty / 'null' / 'undefined' (case-insensitive) → null.</summary>
    private static string? NormalizeUuid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = value.Trim();
        if (v.Length == 0) return null;
        var lower = v.ToLowerInvariant();
        if (lower == "null" || lower == "undefined") return null;
        return v;
    }

    // JS String.prototype.localeCompare sign; ordinal is close enough for lowercased ascii names +
    // lowercase uuid hex ids used as sort keys. // PARITY-TODO: exact ICU collation semantics.
    private static int LocaleCompare(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));

    private sealed class InternalNode
    {
        public required OrgHierarchyInput Org;
        public string? ParentId;
        public readonly HashSet<string> Children = new();
    }

    /// <summary>
    /// Pure hierarchy computation. Reproduces the upstream child-sort tiebreak quirk exactly:
    /// when names differ the comparator compares child a's lowercased NAME against child b's ID.
    /// </summary>
    public static ComputedHierarchy Compute(IEnumerable<OrgHierarchyInput> organizations, string tenantId)
    {
        // Preserve insertion order (JS Map iteration order) for the root/orphan walk loops.
        var order = new List<string>();
        var nodes = new Dictionary<string, InternalNode>();

        foreach (var org in organizations)
        {
            var id = org.Id;
            if (nodes.ContainsKey(id)) { nodes[id] = new InternalNode { Org = org, ParentId = NormalizeUuid(org.ParentId) }; continue; }
            nodes[id] = new InternalNode { Org = org, ParentId = NormalizeUuid(org.ParentId) };
            order.Add(id);
        }

        // Establish child relationships (ignore missing parents or self-references).
        foreach (var id in order)
        {
            var node = nodes[id];
            var parentId = node.ParentId;
            if (parentId is null || parentId == id) { node.ParentId = null; continue; }
            if (!nodes.TryGetValue(parentId, out var parent)) { node.ParentId = null; continue; }
            parent.Children.Add(id);
        }

        var computed = new Dictionary<string, ComputedOrganizationNode>();
        var orderedIds = new List<string>();
        var orderedSet = new HashSet<string>();
        var visited = new HashSet<string>();

        List<string> Walk(string nodeId, List<string> ancestors)
        {
            if (ancestors.Contains(nodeId))
            {
                // Cycle detected; break by treating as root.
                var cyc = nodes.TryGetValue(nodeId, out var cn) ? cn : null;
                var orgName = cyc?.Org.Name ?? "";
                computed[nodeId] = new ComputedOrganizationNode(
                    nodeId, tenantId, orgName, orgName, null, 0, nodeId, nodeId,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                    cyc?.Org.IsActive ?? true);
                if (orderedSet.Add(nodeId)) orderedIds.Add(nodeId);
                visited.Add(nodeId);
                return new List<string>();
            }

            if (!nodes.TryGetValue(nodeId, out var node)) return new List<string>();

            visited.Add(nodeId);
            var org = node.Org;
            var id = org.Id;
            var nextAncestors = new List<string>(ancestors) { id };
            if (orderedSet.Add(id)) orderedIds.Add(id);

            var childIds = node.Children.Where(nodes.ContainsKey).ToList();
            childIds.Sort((a, b) =>
            {
                var an = nodes[a].Org.Name.ToLowerInvariant();
                var bn = nodes[b].Org.Name.ToLowerInvariant();
                if (an == bn) return LocaleCompare(a, b);
                return LocaleCompare(an, b);
            });

            var descendantIds = new List<string>();
            foreach (var childId in childIds)
            {
                var desc = Walk(childId, nextAncestors);
                descendantIds.Add(childId);
                descendantIds.AddRange(desc);
            }

            var ancestorIds = ancestors;
            var depth = ancestorIds.Count;
            var rootId = ancestorIds.Count > 0 ? ancestorIds[0] : id;
            var treePath = string.Join("/", nextAncestors);
            var ancestorNames = ancestors
                .Select(aid => nodes.TryGetValue(aid, out var an) ? an.Org.Name : null)
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!);
            var pathLabel = string.Join(" / ", ancestorNames.Append(org.Name));

            computed[id] = new ComputedOrganizationNode(
                id, tenantId, org.Name, pathLabel, node.ParentId, depth, rootId, treePath,
                ancestorIds, childIds, descendantIds, org.IsActive);
            return descendantIds;
        }

        // Walk roots first (nodes without parent or whose parent is missing).
        foreach (var id in order)
        {
            var node = nodes[id];
            if (node.ParentId is null || !nodes.ContainsKey(node.ParentId))
                Walk(id, new List<string>());
        }
        // Handle orphaned nodes or cycles not reached above.
        foreach (var id in order)
        {
            if (!visited.Contains(id)) Walk(id, new List<string>());
        }

        var ordered = orderedIds
            .Where(computed.ContainsKey)
            .Select(id => computed[id])
            .ToList();

        return new ComputedHierarchy(computed, ordered);
    }

    /// <summary>
    /// EF port of rebuildHierarchyForTenant: loads active orgs (ordered name ASC), computes the tree,
    /// writes back parent/root/treePath/depth/ancestor/child/descendant + updatedAt=now, then saves.
    /// Orgs not present in the computed map reset to self-root defaults.
    /// </summary>
    public static async Task<ComputedHierarchy> RebuildForTenantAsync(
        AppDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var organizations = await db.Set<Organization>()
            .Where(o => o.TenantId == tenantId && o.DeletedAt == null)
            .OrderBy(o => o.Name)
            .ToListAsync(ct);

        var inputs = organizations.Select(o => new OrgHierarchyInput(
            o.Id.ToString(), o.ParentId?.ToString(), o.Name, o.IsActive));
        var hierarchy = Compute(inputs, tenantId.ToString());
        var now = DateTimeOffset.UtcNow;

        foreach (var org in organizations)
        {
            if (!hierarchy.Map.TryGetValue(org.Id.ToString(), out var computed))
            {
                org.ParentId = null;
                org.RootId = org.Id;
                org.TreePath = org.Id.ToString();
                org.Depth = 0;
                org.AncestorIdsJson = "[]";
                org.ChildIdsJson = "[]";
                org.DescendantIdsJson = "[]";
                org.UpdatedAt = now;
                continue;
            }
            org.ParentId = ParseGuid(computed.ParentId);
            org.RootId = ParseGuid(computed.RootId);
            org.TreePath = computed.TreePath;
            org.Depth = computed.Depth;
            org.AncestorIdsJson = JsonSerializer.Serialize(computed.AncestorIds);
            org.ChildIdsJson = JsonSerializer.Serialize(computed.ChildIds);
            org.DescendantIdsJson = JsonSerializer.Serialize(computed.DescendantIds);
            org.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return hierarchy;
    }

    /// <summary>
    /// resolveUniqueSlug: candidate = baseSlug; up to 50 iterations checking for a free/owned slug
    /// per tenant; on conflict appends -1, -2, …; after 50 → baseSlug-&lt;unix-ms&gt;.
    /// </summary>
    public static async Task<string> ResolveUniqueSlugAsync(
        AppDbContext db, Guid tenantId, string baseSlug, Guid? excludeId = null, CancellationToken ct = default)
    {
        var candidate = baseSlug;
        for (var i = 0; i < 50; i++)
        {
            var existing = await db.Set<Organization>().AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Slug == candidate && o.DeletedAt == null, ct);
            if (existing is null || (excludeId is { } ex && existing.Id == ex)) return candidate;
            candidate = $"{baseSlug}-{i + 1}";
        }
        return $"{baseSlug}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private static Guid? ParseGuid(string? value) =>
        !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var g) ? g : null;
}
