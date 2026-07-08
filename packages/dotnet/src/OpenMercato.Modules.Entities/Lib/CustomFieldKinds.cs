namespace OpenMercato.Modules.Entities.Lib;

/// <summary>
/// The custom-field kind catalog + kind→storage-column mapping — the port of
/// <c>shared/modules/entities/kinds.ts</c> and <c>lib/helpers.ts::columnFromKind</c>.
/// </summary>
public static class CustomFieldKinds
{
    /// <summary>CUSTOM_FIELD_KINDS (kinds.ts), in declaration order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        "text", "multiline", "integer", "float", "boolean",
        "select", "currency", "relation", "attachment", "dictionary",
        "date", "datetime",
    };

    public const string CurrencyOptionsUrl = "/api/currencies/currencies/options";

    public static bool IsKind(string kind) => All.Contains(kind);

    /// <summary>Which <c>custom_field_values</c> column stores this kind (helpers.ts::columnFromKind).</summary>
    public static ValueColumn ColumnFromKind(string kind) => kind switch
    {
        "text" or "select" or "currency" or "dictionary" => ValueColumn.Text,
        "multiline" => ValueColumn.Multiline,
        "integer" => ValueColumn.Int,
        "float" => ValueColumn.Float,
        "boolean" => ValueColumn.Bool,
        _ => ValueColumn.Text,
    };
}

/// <summary>The five typed value columns on <c>custom_field_values</c>.</summary>
public enum ValueColumn { Text, Multiline, Int, Float, Bool }
