using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Lib;

/// <summary>
/// Materialized-path tree computation for catalog categories — the port of upstream
/// <c>lib/categoryHierarchy.ts</c>. <see cref="Compute"/> walks the in-scope categories and derives each
/// node's depth / rootId / treePath / ancestorIds / childIds (name-sorted) / descendantIds / pathLabel;
/// <see cref="RebuildAsync"/> writes those columns back to every row (called after any category mutation,
/// mirroring <c>rebuildCategoryHierarchyForOrganization</c>). Cycles and dangling parents are treated as
/// roots, exactly as upstream.
/// </summary>
public static class CategoryHierarchy
{
    public sealed class Node
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? ParentId { get; set; }
        public int Depth { get; set; }
        public string RootId { get; set; } = string.Empty;
        public string TreePath { get; set; } = string.Empty;
        public string PathLabel { get; set; } = string.Empty;
        public List<string> AncestorIds { get; set; } = new();
        public List<string> ChildIds { get; set; } = new();
        public List<string> DescendantIds { get; set; } = new();
        public bool IsActive { get; set; }
    }

    public sealed class Result
    {
        public required Dictionary<string, Node> Map { get; init; }
        public required List<Node> Ordered { get; init; }
    }

    private sealed class Internal
    {
        public required CatalogProductCategory Category { get; init; }
        public string? ParentId { get; set; }
        public SortedSet<string> Children { get; } = new(StringComparer.Ordinal);
    }

    private static string? NormalizeId(Guid? value)
    {
        if (value is null || value == Guid.Empty) return null;
        return value.Value.ToString();
    }

    public static Result Compute(IReadOnlyList<CatalogProductCategory> categories)
    {
        var nodes = new Dictionary<string, Internal>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            var id = category.Id.ToString();
            nodes[id] = new Internal { Category = category, ParentId = NormalizeId(category.ParentId) };
        }

        // Wire parent → children; a missing/self/out-of-scope parent makes the node a root.
        foreach (var (id, node) in nodes)
        {
            var parentId = node.ParentId;
            if (parentId is null || parentId == id || !nodes.ContainsKey(parentId))
            {
                node.ParentId = null;
                continue;
            }
            nodes[parentId].Children.Add(id);
        }

        var computed = new Dictionary<string, Node>(StringComparer.Ordinal);
        var orderedIds = new List<string>();
        var orderedSet = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // Children are ordered by lower-cased name, ties broken by id (upstream sort).
        List<string> SortedChildren(Internal node) => node.Children
            .Where(nodes.ContainsKey)
            .OrderBy(cid => nodes[cid].Category.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(cid => cid, StringComparer.Ordinal)
            .ToList();

        List<string> Walk(string nodeId, List<string> ancestors)
        {
            if (ancestors.Contains(nodeId))
            {
                // Cycle: collapse to a self-root, matching upstream.
                if (nodes.TryGetValue(nodeId, out var cyclic))
                {
                    computed[nodeId] = new Node
                    {
                        Id = nodeId,
                        Name = cyclic.Category.Name,
                        PathLabel = cyclic.Category.Name,
                        ParentId = null,
                        Depth = 0,
                        RootId = nodeId,
                        TreePath = nodeId,
                        IsActive = cyclic.Category.IsActive,
                    };
                    if (orderedSet.Add(nodeId)) orderedIds.Add(nodeId);
                }
                visited.Add(nodeId);
                return new List<string>();
            }

            if (!nodes.TryGetValue(nodeId, out var node)) return new List<string>();
            visited.Add(nodeId);
            var id = node.Category.Id.ToString();
            var nextAncestors = new List<string>(ancestors) { id };
            if (orderedSet.Add(id)) orderedIds.Add(id);

            var childIds = SortedChildren(node);
            var descendantIds = new List<string>();
            foreach (var childId in childIds)
            {
                var desc = Walk(childId, nextAncestors);
                descendantIds.Add(childId);
                descendantIds.AddRange(desc);
            }

            var depth = ancestors.Count;
            var rootId = ancestors.Count > 0 ? ancestors[0] : id;
            var treePath = string.Join('/', nextAncestors);
            var ancestorNames = ancestors
                .Select(aid => nodes.TryGetValue(aid, out var a) ? a.Category.Name : null)
                .Where(n => !string.IsNullOrEmpty(n))!
                .ToList();
            ancestorNames.Add(node.Category.Name);
            var pathLabel = string.Join(" / ", ancestorNames);

            computed[id] = new Node
            {
                Id = id,
                Name = node.Category.Name,
                PathLabel = pathLabel,
                ParentId = node.ParentId,
                Depth = depth,
                RootId = rootId,
                TreePath = treePath,
                AncestorIds = new List<string>(ancestors),
                ChildIds = childIds,
                DescendantIds = descendantIds,
                IsActive = node.Category.IsActive,
            };
            return descendantIds;
        }

        foreach (var (id, node) in nodes)
            if (node.ParentId is null || !nodes.ContainsKey(node.ParentId))
                Walk(id, new List<string>());

        foreach (var id in nodes.Keys)
            if (!visited.Contains(id))
                Walk(id, new List<string>());

        var ordered = orderedIds.Where(computed.ContainsKey).Select(id => computed[id]).ToList();
        return new Result { Map = computed, Ordered = ordered };
    }

    /// <summary>Recompute + persist every in-scope category's materialized-path columns. Call after any
    /// category create/update/delete (upstream <c>rebuildCategoryHierarchyForOrganization</c>).</summary>
    public static async Task<Result> RebuildAsync(AppDbContext db, Guid organizationId, Guid tenantId, CancellationToken ct = default)
    {
        var categories = await db.Set<CatalogProductCategory>()
            .Where(c => c.OrganizationId == organizationId && c.TenantId == tenantId && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        var hierarchy = Compute(categories);
        var now = DateTimeOffset.UtcNow;
        foreach (var category in categories)
        {
            var id = category.Id.ToString();
            if (hierarchy.Map.TryGetValue(id, out var computed))
            {
                category.ParentId = ParseGuid(computed.ParentId);
                category.RootId = ParseGuid(computed.RootId);
                category.TreePath = computed.TreePath;
                category.Depth = computed.Depth;
                category.AncestorIds = SerializeIds(computed.AncestorIds);
                category.ChildIds = SerializeIds(computed.ChildIds);
                category.DescendantIds = SerializeIds(computed.DescendantIds);
            }
            else
            {
                category.ParentId = null;
                category.RootId = category.Id;
                category.TreePath = id;
                category.Depth = 0;
                category.AncestorIds = "[]";
                category.ChildIds = "[]";
                category.DescendantIds = "[]";
            }
            category.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return hierarchy;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var g) ? g : null;

    private static string SerializeIds(IReadOnlyList<string> ids) =>
        System.Text.Json.JsonSerializer.Serialize(ids);
}
