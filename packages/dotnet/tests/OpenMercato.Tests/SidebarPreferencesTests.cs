using System.Text.Json;
using OpenMercato.Modules.Auth.Services;
using Xunit;

namespace OpenMercato.Tests;

public class SidebarVariantAutoNameTests
{
    [Fact]
    public void Blank_library_uses_base_name()
    {
        Assert.Equal("My preferences", SidebarPreferencesService.ComputeAutoName(Array.Empty<string>()));
    }

    [Fact]
    public void Base_name_taken_increments_to_two()
    {
        Assert.Equal("My preferences 2", SidebarPreferencesService.ComputeAutoName(new[] { "My preferences" }));
    }

    [Fact]
    public void Sequential_names_increment_to_next_free_number()
    {
        var names = new[] { "My preferences", "My preferences 2" };
        Assert.Equal("My preferences 3", SidebarPreferencesService.ComputeAutoName(names));
    }

    [Fact]
    public void Missing_base_name_prefers_base_over_numbered()
    {
        // Upstream: if number 1 (the bare prefix) is unused, return the bare prefix.
        Assert.Equal("My preferences", SidebarPreferencesService.ComputeAutoName(new[] { "My preferences 2" }));
    }

    [Fact]
    public void Fills_the_lowest_free_gap()
    {
        var names = new[] { "My preferences", "My preferences 3" };
        Assert.Equal("My preferences 2", SidebarPreferencesService.ComputeAutoName(names));
    }

    [Fact]
    public void Ignores_unrelated_and_malformed_names()
    {
        var names = new[] { "My preferences", "Other", "My preferences x", "My preferences 02x" };
        // Only the bare prefix counts (=> 1 used); next free is 2.
        Assert.Equal("My preferences 2", SidebarPreferencesService.ComputeAutoName(names));
    }

    [Fact]
    public void Honors_a_custom_prefix()
    {
        Assert.Equal("Layout 2", SidebarPreferencesService.ComputeAutoName(new[] { "Layout" }, "Layout"));
    }
}

public class SidebarSettingsShapeTests
{
    [Fact]
    public void Default_settings_have_version_two_and_empty_collections()
    {
        var s = SidebarSettings.Default();

        Assert.Equal(2, s.Version);
        Assert.Empty(s.GroupOrder);
        Assert.Empty(s.GroupLabels);
        Assert.Empty(s.ItemLabels);
        Assert.Empty(s.HiddenItems);
        Assert.Empty(s.ItemOrder);
    }

    [Fact]
    public void Normalize_null_equals_default()
    {
        var s = SidebarSettings.Normalize(null);
        Assert.Equal(2, s.Version);
        Assert.Empty(s.GroupOrder);
    }

    [Fact]
    public void Default_serializes_with_exact_camelCase_keys()
    {
        var json = SidebarSettings.Default().ToJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("groupOrder").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("groupLabels").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("itemLabels").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("hiddenItems").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("itemOrder").ValueKind);
    }

    [Fact]
    public void HiddenItems_are_trimmed_deduped_and_empties_dropped()
    {
        var s = SidebarSettings.NormalizeCore(
            version: null,
            groupOrder: null,
            groupLabels: null,
            itemLabels: null,
            hiddenItems: new[] { " a ", "a", "", "  ", "b" },
            itemOrder: null);

        Assert.Equal(new[] { "a", "b" }, s.HiddenItems);
    }

    [Fact]
    public void GroupOrder_is_kept_verbatim_without_trim_or_dedupe()
    {
        // normalizeSidebarSettings only filters non-strings for groupOrder — no trim, no dedupe.
        var s = SidebarSettings.NormalizeCore(
            version: null,
            groupOrder: new[] { " x ", " x ", "y" },
            groupLabels: null,
            itemLabels: null,
            hiddenItems: null,
            itemOrder: null);

        Assert.Equal(new[] { " x ", " x ", "y" }, s.GroupOrder);
    }

    [Fact]
    public void ItemOrder_drops_keys_whose_list_normalizes_to_empty()
    {
        var s = SidebarSettings.NormalizeCore(
            version: null,
            groupOrder: null,
            groupLabels: null,
            itemLabels: null,
            hiddenItems: null,
            itemOrder: new[]
            {
                new KeyValuePair<string, IEnumerable<string>>("keep", new[] { "a", "a", " b " }),
                new KeyValuePair<string, IEnumerable<string>>("drop", new[] { "", "  " }),
            });

        Assert.True(s.ItemOrder.ContainsKey("keep"));
        Assert.False(s.ItemOrder.ContainsKey("drop"));
        Assert.Equal(new[] { "a", "b" }, s.ItemOrder["keep"]);
    }

    [Fact]
    public void Parse_round_trips_a_stored_settings_document()
    {
        var original = SidebarSettings.NormalizeCore(
            version: 2,
            groupOrder: new[] { "g1" },
            groupLabels: new[] { new KeyValuePair<string, string>("g1", "Group One") },
            itemLabels: null,
            hiddenItems: new[] { "h1" },
            itemOrder: new[] { new KeyValuePair<string, IEnumerable<string>>("g1", new[] { "i1" }) });

        var parsed = SidebarSettings.Parse(original.ToJson());

        Assert.Equal(2, parsed.Version);
        Assert.Equal(new[] { "g1" }, parsed.GroupOrder);
        Assert.Equal("Group One", parsed.GroupLabels["g1"]);
        Assert.Equal(new[] { "h1" }, parsed.HiddenItems);
        Assert.Equal(new[] { "i1" }, parsed.ItemOrder["g1"]);
    }
}
