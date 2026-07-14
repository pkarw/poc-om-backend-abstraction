namespace OpenMercato.Modules.Catalog.Lib;

/// <summary>
/// Unit-code canonicalization — the port of upstream <c>packages/shared/src/lib/units/unitCodes.ts</c>.
/// Trims + lower-cases a unit code and maps the legacy alias <c>qty → pc</c>; used both to canonicalize a
/// product's <c>default_unit</c> and to look up its conversion rows by unit.
/// </summary>
public static class CatalogUnitCodes
{
    public static readonly string[] ReferenceUnitCodes = { "kg", "l", "m2", "m3", "pc" };

    /// <summary>Trim + lower-case, applying the <c>qty → pc</c> legacy alias. Null/blank → null.</summary>
    public static string? Canonicalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var lower = trimmed.ToLowerInvariant();
        return lower == "qty" ? "pc" : lower;
    }
}
