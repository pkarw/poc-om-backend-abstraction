using System.Text.RegularExpressions;
using OpenMercato.Modules.Dictionaries.Data;

namespace OpenMercato.Modules.Dictionaries.Lib;

/// <summary>Entry sort modes — the port of upstream <c>lib/entrySort.ts</c>.</summary>
public static class DictionaryEntrySortModes
{
    public const string LabelAsc = "label_asc";
    public const string LabelDesc = "label_desc";
    public const string ValueAsc = "value_asc";
    public const string ValueDesc = "value_desc";
    public const string CreatedAtAsc = "created_at_asc";
    public const string CreatedAtDesc = "created_at_desc";

    public const string Default = LabelAsc;

    public static readonly IReadOnlyList<string> All = new[]
    {
        LabelAsc, LabelDesc, ValueAsc, ValueDesc, CreatedAtAsc, CreatedAtDesc,
    };

    /// <summary>Coerce an arbitrary value to a valid mode (upstream <c>resolveDictionaryEntrySortMode</c>).</summary>
    public static string Resolve(string? value)
        => value is not null && All.Contains(value) ? value : Default;
}

/// <summary>
/// Value normalization + appearance sanitation — the port of upstream <c>lib/utils.ts</c> and the
/// sort helpers in <c>lib/entrySort.ts</c>. Pure functions, unit-tested directly.
/// </summary>
public static class DictionaryUtils
{
    private static readonly Regex HexColor = new("^#([0-9a-fA-F]{6})$", RegexOptions.Compiled);

    /// <summary><c>trim().toLowerCase()</c> — the dedupe key for entries.</summary>
    public static string NormalizeValue(string value) => value.Trim().ToLowerInvariant();

    /// <summary>Return a lowercased <c>#rrggbb</c> or null (invalid/empty → null).</summary>
    public static string? SanitizeColor(string? color)
    {
        if (string.IsNullOrEmpty(color)) return null;
        var trimmed = color.Trim();
        if (trimmed.Length == 0) return null;
        var m = HexColor.Match(trimmed);
        if (!m.Success) return null;
        return "#" + m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>Return a trimmed icon token clipped to 64 chars, or null.</summary>
    public static string? SanitizeIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return null;
        var trimmed = icon.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }

    /// <summary>
    /// Stable sort of entries by the dictionary's mode (upstream <c>sortDictionaryEntries</c>).
    /// Ties broken by id (ordinal, case-insensitive), matching the JS <c>localeCompare</c> fallback.
    /// </summary>
    public static IReadOnlyList<DictionaryEntry> Sort(IEnumerable<DictionaryEntry> entries, string mode)
    {
        var list = entries.ToList();
        list.Sort((a, b) =>
        {
            var primary = Compare(a, b, mode);
            if (primary != 0) return primary;
            return string.Compare(a.Id.ToString(), b.Id.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static int Compare(DictionaryEntry left, DictionaryEntry right, string mode) => mode switch
    {
        DictionaryEntrySortModes.LabelDesc => Text(Coalesce(right.Label, right.Value), Coalesce(left.Label, left.Value)),
        DictionaryEntrySortModes.ValueAsc => Text(left.Value, right.Value),
        DictionaryEntrySortModes.ValueDesc => Text(right.Value, left.Value),
        DictionaryEntrySortModes.CreatedAtAsc => left.CreatedAt.CompareTo(right.CreatedAt),
        DictionaryEntrySortModes.CreatedAtDesc => right.CreatedAt.CompareTo(left.CreatedAt),
        _ => Text(Coalesce(left.Label, left.Value), Coalesce(right.Label, right.Value)),
    };

    private static string Coalesce(string? a, string? b) => !string.IsNullOrEmpty(a) ? a! : (b ?? string.Empty);

    // Case-insensitive, base-sensitivity compare (mirrors localeCompare(..., { sensitivity: 'base' })).
    private static int Text(string? a, string? b) =>
        string.Compare(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}
