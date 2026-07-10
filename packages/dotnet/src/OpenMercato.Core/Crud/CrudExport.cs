using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMercato.Core.Crud;

/// <summary>A single export column: the row key (<see cref="Field"/>) and its human header.</summary>
public sealed record CrudExportColumn(string Field, string Header);

/// <summary>Columns + rows ready to serialize (port of upstream <c>PreparedExport</c>).</summary>
public sealed record PreparedExport(
    IReadOnlyList<CrudExportColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>A serialized export body plus its content type and file extension (port of <c>SerializedExport</c>).</summary>
public sealed record SerializedExport(string Body, string ContentType, string FileExtension);

/// <summary>
/// Pure, unit-testable port of upstream <c>packages/shared/src/lib/crud/exporters.ts</c> plus the
/// default column-preparation path from <c>factory.ts</c> (<c>buildDefaultExport</c> +
/// <c>prepareExportData</c> + <c>ensureColumns</c>). Serializes a filtered list result set into one of
/// four formats — csv / json / xml / markdown — matching OM byte-for-byte (column union across rows,
/// value normalization, and per-format escaping).
///
/// Note (parity): OM columns carry a <c>header</c> = <see cref="ToHeaderLabel"/>(key). CSV/JSON/Markdown
/// serialize with the human header; XML uses the sanitized field key as the element tag. That mirrors
/// exporters.ts exactly (<c>serializeCsv</c>/<c>serializeJson</c>/<c>serializeXml</c>/<c>serializeMarkdown</c>).
/// </summary>
public static class CrudExport
{
    public const string Csv = "csv";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Markdown = "markdown";

    /// <summary>The four formats OM enables by default.</summary>
    public static readonly IReadOnlyList<string> AllFormats = new[] { Csv, Json, Xml, Markdown };

    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string JsonContentType = "application/json; charset=utf-8";
    private const string XmlContentType = "application/xml; charset=utf-8";
    private const string MarkdownContentType = "text/markdown; charset=utf-8";

    private static readonly JsonSerializerOptions JsonExport = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- Format normalization (port of normalizeExportFormat) ---------------------------------

    /// <summary>Normalize a raw <c>?format=</c> token to a canonical format, or null when unrecognized.</summary>
    public static string? NormalizeFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            "csv" => Csv,
            "json" or "application/json" => Json,
            "xml" or "application/xml" => Xml,
            "markdown" or "md" or "text/markdown" => Markdown,
            _ => null,
        };
    }

    // ---- Value normalization (port of normalizeValue) -----------------------------------------

    private static string NormalizeValue(object? value)
    {
        switch (value)
        {
            case null: return string.Empty;
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case DateTime dt: return ToIso(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime());
            case DateTimeOffset dto: return ToIso(dto.UtcDateTime);
            case Guid g: return g.ToString();
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            case JsonElement je: return NormalizeJsonElement(je);
        }

        // Arrays / enumerables → join non-empty normalized parts with ', ' (JS: map→filter(Boolean)→join).
        if (value is IDictionary)
            return JsonSerializer.Serialize(value, JsonExportCompact);
        if (value is IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                var n = NormalizeValue(item);
                if (!string.IsNullOrEmpty(n)) parts.Add(n);
            }
            return string.Join(", ", parts);
        }

        // Any other object → JSON (JS: JSON.stringify).
        return JsonSerializer.Serialize(value, JsonExportCompact);
    }

    private static readonly JsonSerializerOptions JsonExportCompact = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string NormalizeJsonElement(JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;
            case JsonValueKind.String:
                return je.GetString() ?? string.Empty;
            case JsonValueKind.Number:
                return je.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Array:
            {
                var parts = new List<string>();
                foreach (var el in je.EnumerateArray())
                {
                    var n = NormalizeJsonElement(el);
                    if (!string.IsNullOrEmpty(n)) parts.Add(n);
                }
                return string.Join(", ", parts);
            }
            default:
                return je.GetRawText();
        }
    }

    private static string ToIso(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ---- Escaping (ports of escapeCsv / escapeMarkdown / escapeXmlTag / escapeXmlValue) --------

    private static readonly char[] CsvSpecials = { '"', ',', '\n', '\r' };

    private static string EscapeCsv(string value) =>
        value.IndexOfAny(CsvSpecials) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string EscapeMarkdown(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("|", "\\|");
        escaped = Regex.Replace(escaped, "\r?\n", "<br />");
        return escaped.Length == 0 ? " " : escaped;
    }

    private static string EscapeXmlTag(string tag, int fallbackIndex)
    {
        var sanitized = Regex.Replace(tag, "[^A-Za-z0-9_:-]", "_");
        var normalized = sanitized.Length > 0 ? sanitized : $"field_{fallbackIndex}";
        return Regex.IsMatch(normalized, "^[^A-Za-z_]") ? "f_" + normalized : normalized;
    }

    private static string EscapeXmlValue(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    // ---- Column preparation (ports of toHeaderLabel / ensureColumns / buildDefaultExport) ------

    /// <summary>Humanize a row key into a header (strip a leading <c>cf</c> prefix, title-case words).</summary>
    public static string ToHeaderLabel(string key)
    {
        var withoutPrefix = Regex.Replace(key, @"^cf[:_\-\s]+", string.Empty, RegexOptions.IgnoreCase);
        var normalized = Regex.Replace(withoutPrefix, @"[_\-\s]+", " ").Trim();
        if (normalized.Length == 0) return "Field";
        return Regex.Replace(normalized, @"\b\w", m => m.Value.ToUpperInvariant());
    }

    /// <summary>
    /// Compute the export columns = the ordered union of keys across all rows (port of
    /// <c>ensureColumns</c>). When there are no rows, falls back to a single <c>id</c> column.
    /// </summary>
    public static IReadOnlyList<CrudExportColumn> EnsureColumns(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var used = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is null) continue;
            foreach (var key in row.Keys)
            {
                // Internal custom-field aggregate shapes are not display columns (the flat cf_<key>
                // values already carry the data); a bare <key> duplicate of a cf_<key> is dropped so
                // the export mirrors OM's one-column-per-field shape.
                if (key is "customValues" or "customFields") continue;
                if (seen.Contains("cf_" + key)) continue; // bare duplicate of an emitted cf_ column
                if (seen.Add(key)) used.Add(key);
            }
        }
        // Drop any bare key whose cf_ variant is also present (order-independent second pass).
        var cfBare = new HashSet<string>(used.Where(k => k.StartsWith("cf_", StringComparison.Ordinal)).Select(k => k[3..]), StringComparer.Ordinal);
        used = used.Where(k => !cfBare.Contains(k)).ToList();
        if (used.Count == 0) return new[] { new CrudExportColumn("id", "ID") };
        return used.Select(key => new CrudExportColumn(key, ToHeaderLabel(key))).ToList();
    }

    /// <summary>
    /// The default export preparation path (port of <c>buildDefaultExport</c> + <c>prepareExportData</c>
    /// with no column/csv config): rows pass through as-is and columns are the union of their keys.
    /// </summary>
    public static PreparedExport PrepareDefault(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        new(EnsureColumns(rows), rows);

    // ---- Serialization (ports of serializeCsv / serializeJson / serializeXml / serializeMarkdown) ----

    public static SerializedExport Serialize(PreparedExport prepared, string format) => format switch
    {
        Csv => SerializeCsv(prepared),
        Json => SerializeJson(prepared),
        Xml => SerializeXml(prepared),
        Markdown => SerializeMarkdown(prepared),
        _ => SerializeJson(prepared),
    };

    private static object? Get(IReadOnlyDictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out var v) ? v : null;

    private static SerializedExport SerializeCsv(PreparedExport prepared)
    {
        var lines = new List<string> { string.Join(",", prepared.Columns.Select(c => c.Header)) };
        foreach (var row in prepared.Rows)
            lines.Add(string.Join(",", prepared.Columns.Select(c => EscapeCsv(NormalizeValue(Get(row, c.Field))))));
        return new SerializedExport(string.Join("\n", lines), CsvContentType, "csv");
    }

    private static SerializedExport SerializeJson(PreparedExport prepared)
    {
        var payload = prepared.Rows.Select(row =>
        {
            var obj = new Dictionary<string, object?>();
            foreach (var column in prepared.Columns)
                obj[column.Header] = Get(row, column.Field);
            return obj;
        }).ToList();
        return new SerializedExport(JsonSerializer.Serialize(payload, JsonExport), JsonContentType, "json");
    }

    private static SerializedExport SerializeXml(PreparedExport prepared)
    {
        var lines = new List<string> { "<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "<records>" };
        foreach (var row in prepared.Rows)
        {
            lines.Add("  <record>");
            for (var i = 0; i < prepared.Columns.Count; i++)
            {
                var column = prepared.Columns[i];
                var tag = EscapeXmlTag(string.IsNullOrEmpty(column.Field) ? column.Header : column.Field, i);
                var value = EscapeXmlValue(NormalizeValue(Get(row, column.Field)));
                lines.Add($"    <{tag}>{value}</{tag}>");
            }
            lines.Add("  </record>");
        }
        lines.Add("</records>");
        return new SerializedExport(string.Join("\n", lines), XmlContentType, "xml");
    }

    private static SerializedExport SerializeMarkdown(PreparedExport prepared)
    {
        var headerLine = "| " + string.Join(" | ", prepared.Columns.Select(c => EscapeMarkdown(c.Header))) + " |";
        var dividerLine = "| " + string.Join(" | ", prepared.Columns.Select(_ => "---")) + " |";
        var lines = new List<string> { headerLine, dividerLine };
        foreach (var row in prepared.Rows)
            lines.Add("| " + string.Join(" | ", prepared.Columns.Select(c => EscapeMarkdown(NormalizeValue(Get(row, c.Field))))) + " |");
        return new SerializedExport(string.Join("\n", lines), MarkdownContentType, "md");
    }

    // ---- Filename (port of defaultExportFilename) ---------------------------------------------

    /// <summary>Build the download filename <c>&lt;safeBase&gt;.&lt;ext&gt;</c> (ext = format, markdown→md).</summary>
    public static string DefaultFilename(string? baseName, string format)
    {
        var raw = string.IsNullOrWhiteSpace(baseName) ? "export" : baseName!.Trim();
        var safeBase = Regex.Replace(raw, "[^a-z0-9_\\-]", "_", RegexOptions.IgnoreCase);
        var suffix = format == Markdown ? "md" : format;
        return $"{safeBase}.{suffix}";
    }
}
