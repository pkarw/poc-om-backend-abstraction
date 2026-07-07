using OpenMercato.Modules.Directory.Lib;
using Xunit;

namespace OpenMercato.Tests.Directory;

/// <summary>
/// Behavior tests for the pure hierarchy engine (1:1 port of lib/hierarchy.ts): depth/ancestor/
/// descendant computation, cycle breaking, name-sort tiebreak, and slugify.
/// </summary>
public class OrganizationHierarchyTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static OrgHierarchyInput Org(string id, string? parent, string name, bool active = true)
        => new(id, parent, name, active);

    [Fact]
    public void Computes_depth_root_and_ancestor_descendant_arrays()
    {
        // root -> child -> grandchild
        var root = "a0000000-0000-0000-0000-000000000000";
        var child = "b0000000-0000-0000-0000-000000000000";
        var grand = "c0000000-0000-0000-0000-000000000000";
        var result = OrganizationHierarchy.Compute(new[]
        {
            Org(root, null, "Root"),
            Org(child, root, "Child"),
            Org(grand, child, "Grand"),
        }, Tenant);

        var r = result.Map[root];
        Assert.Equal(0, r.Depth);
        Assert.Equal(root, r.RootId);
        Assert.Empty(r.AncestorIds);
        Assert.Equal(new[] { child }, r.ChildIds);
        Assert.Equal(new[] { child, grand }, r.DescendantIds);
        Assert.Equal("Root", r.PathLabel);

        var g = result.Map[grand];
        Assert.Equal(2, g.Depth);
        Assert.Equal(root, g.RootId);
        Assert.Equal(new[] { root, child }, g.AncestorIds);
        Assert.Equal($"{root}/{child}/{grand}", g.TreePath);
        Assert.Equal("Root / Child / Grand", g.PathLabel);
    }

    [Fact]
    public void Missing_or_self_parent_is_treated_as_root()
    {
        var a = "a0000000-0000-0000-0000-000000000000";
        var b = "b0000000-0000-0000-0000-000000000000";
        var result = OrganizationHierarchy.Compute(new[]
        {
            Org(a, a, "Self"),                                   // self-parent -> root
            Org(b, "f0000000-0000-0000-0000-000000000000", "MissingParent"), // missing parent -> root
        }, Tenant);

        Assert.Equal(0, result.Map[a].Depth);
        Assert.Null(result.Map[a].ParentId);
        Assert.Equal(0, result.Map[b].Depth);
        Assert.Null(result.Map[b].ParentId);
    }

    [Fact]
    public void Children_sorted_by_lowercased_name()
    {
        var root = "a0000000-0000-0000-0000-000000000000";
        var c1 = "d0000000-0000-0000-0000-000000000000"; // "Zebra"
        var c2 = "e0000000-0000-0000-0000-000000000000"; // "alpha"
        var result = OrganizationHierarchy.Compute(new[]
        {
            Org(root, null, "Root"),
            Org(c1, root, "Zebra"),
            Org(c2, root, "alpha"),
        }, Tenant);

        // 'alpha' < 'zebra' lowercased => c2 before c1.
        Assert.Equal(new[] { c2, c1 }, result.Map[root].ChildIds);
    }

    [Fact]
    public void Cycle_is_broken_into_standalone_root()
    {
        var a = "a0000000-0000-0000-0000-000000000000";
        var b = "b0000000-0000-0000-0000-000000000000";
        var result = OrganizationHierarchy.Compute(new[]
        {
            Org(a, b, "A"),
            Org(b, a, "B"),
        }, Tenant);

        // Both reachable; the engine must terminate and produce nodes for both.
        Assert.True(result.Map.ContainsKey(a));
        Assert.True(result.Map.ContainsKey(b));
        Assert.Equal(2, result.Ordered.Count);
    }

    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("  Hello  World  ", "hello-world")]
    [InlineData("Über Cafe!", "ber-cafe")]
    [InlineData("---", "")]
    public void Slugify_matches_upstream(string input, string expected)
        => Assert.Equal(expected, Slugify.Run(input));
}
