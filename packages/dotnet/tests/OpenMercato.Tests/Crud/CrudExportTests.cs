using System.Text.Json;
using OpenMercato.Core.Crud;
using Xunit;

namespace OpenMercato.Tests.Crud;

/// <summary>
/// Unit tests for the pure export serializer (OpenMercato.Core.Crud.CrudExport) — the port of upstream
/// packages/shared/src/lib/crud/exporters.ts: column union across rows, value normalization, and the
/// per-format escaping/structure for csv / json / xml / markdown.
/// </summary>
public class CrudExportTests
{
    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in cells) d[k] = v;
        return d;
    }

    private static SerializedExport Serialize(string format, params IReadOnlyDictionary<string, object?>[] rows)
        => CrudExport.Serialize(CrudExport.PrepareDefault(rows), format);

    // ---- Format normalization -----------------------------------------------------------------

    [Theory]
    [InlineData("csv", "csv")]
    [InlineData("CSV", "csv")]
    [InlineData("json", "json")]
    [InlineData("application/json", "json")]
    [InlineData("xml", "xml")]
    [InlineData("markdown", "markdown")]
    [InlineData("md", "markdown")]
    [InlineData("text/markdown", "markdown")]
    public void NormalizeFormat_maps_known_aliases(string raw, string expected)
        => Assert.Equal(expected, CrudExport.NormalizeFormat(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yaml")]
    [InlineData(null)]
    public void NormalizeFormat_returns_null_for_unknown(string? raw)
        => Assert.Null(CrudExport.NormalizeFormat(raw));

    // ---- Column union -------------------------------------------------------------------------

    [Fact]
    public void EnsureColumns_is_ordered_union_across_rows_with_differing_keys()
    {
        var cols = CrudExport.EnsureColumns(new[]
        {
            Row(("id", "1"), ("name", "a")),
            Row(("name", "b"), ("email", "b@x.io")),
            Row(("id", "3"), ("phone", "555")),
        });

        // Union in first-seen order: id, name, email, phone.
        Assert.Equal(new[] { "id", "name", "email", "phone" }, cols.Select(c => c.Field).ToArray());
    }

    [Fact]
    public void EnsureColumns_falls_back_to_id_column_when_no_rows()
    {
        var cols = CrudExport.EnsureColumns(Array.Empty<IReadOnlyDictionary<string, object?>>());
        Assert.Single(cols);
        Assert.Equal("id", cols[0].Field);
        Assert.Equal("ID", cols[0].Header);
    }

    [Theory]
    [InlineData("display_name", "Display Name")]
    [InlineData("cf_loyalty_tier", "Loyalty Tier")]
    [InlineData("cf:favColor", "FavColor")]
    [InlineData("id", "Id")]
    public void ToHeaderLabel_humanizes_keys(string key, string expected)
        => Assert.Equal(expected, CrudExport.ToHeaderLabel(key));

    // ---- CSV ----------------------------------------------------------------------------------

    [Fact]
    public void Csv_header_is_column_headers_and_rows_follow()
    {
        var res = Serialize("csv", Row(("id", "1"), ("name", "Ada")));
        Assert.Equal("text/csv; charset=utf-8", res.ContentType);
        Assert.Equal("csv", res.FileExtension);
        var lines = res.Body.Split('\n');
        Assert.Equal("Id,Name", lines[0]);
        Assert.Equal("1,Ada", lines[1]);
    }

    [Fact]
    public void Csv_escapes_comma_quote_and_newline()
    {
        var res = Serialize("csv", Row(
            ("comma", "a,b"),
            ("quote", "she said \"hi\""),
            ("newline", "line1\nline2")));
        // Values are wrapped in quotes; internal quotes doubled; the embedded newline keeps the row multi-line.
        Assert.Contains("\"a,b\"", res.Body);
        Assert.Contains("\"she said \"\"hi\"\"\"", res.Body);
        Assert.Contains("\"line1\nline2\"", res.Body);
    }

    [Fact]
    public void Csv_normalizes_null_array_and_object_values()
    {
        var res = Serialize("csv", Row(
            ("nothing", null),
            ("tags", new object?[] { "x", "y", null }),
            ("meta", new Dictionary<string, object?> { ["k"] = 1 })));
        var valueLine = res.Body.Split('\n')[1];
        // null → empty; array → 'x, y' (nulls filtered, comma-bearing → quoted); object → JSON (quotes doubled + wrapped).
        Assert.StartsWith(",\"x, y\",", valueLine);
        Assert.Contains("{\"\"k\"\":1}", valueLine);
    }

    // ---- JSON ---------------------------------------------------------------------------------

    [Fact]
    public void Json_serializes_rows_keyed_by_header()
    {
        var res = Serialize("json", Row(("id", "1"), ("display_name", "Ada")));
        Assert.Equal("application/json; charset=utf-8", res.ContentType);
        using var doc = JsonDocument.Parse(res.Body);
        var first = doc.RootElement[0];
        Assert.Equal("1", first.GetProperty("Id").GetString());
        Assert.Equal("Ada", first.GetProperty("Display Name").GetString());
    }

    // ---- XML ----------------------------------------------------------------------------------

    [Fact]
    public void Xml_wraps_records_and_escapes_tags_and_values()
    {
        var res = Serialize("xml",
            Row(("id", "1"), ("weird key!", "a & b <c>")),
            Row(("id", "2"), ("weird key!", "x")));
        Assert.Equal("application/xml; charset=utf-8", res.ContentType);
        var lines = res.Body.Split('\n');
        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", lines[0]);
        Assert.Equal("<records>", lines[1]);
        Assert.Equal("  <record>", lines[2]);
        Assert.EndsWith("</records>", res.Body);
        // Tag sanitized (non-name chars → _); value XML-escaped.
        Assert.Contains("<weird_key_>a &amp; b &lt;c&gt;</weird_key_>", res.Body);
        // Two <record> blocks for two rows.
        Assert.Equal(2, res.Body.Split("<record>").Length - 1);
    }

    // ---- Markdown -----------------------------------------------------------------------------

    [Fact]
    public void Markdown_is_github_table_with_header_divider_and_escaped_cells()
    {
        var res = Serialize("markdown",
            Row(("id", "1"), ("note", "a|b\nsecond")),
            Row(("id", "2"), ("email", "z@x.io")));
        Assert.Equal("text/markdown; charset=utf-8", res.ContentType);
        Assert.Equal("md", res.FileExtension);
        var lines = res.Body.Split('\n');
        // Header = union columns humanized; divider row of ---; then one row per record.
        Assert.Equal("| Id | Note | Email |", lines[0]);
        Assert.Equal("| --- | --- | --- |", lines[1]);
        // Pipe escaped, newline → <br />, missing cell for row 1's email is empty.
        Assert.Equal("| 1 | a\\|b<br />second |   |", lines[2]);
        Assert.Equal("| 2 |   | z@x.io |", lines[3]);
    }

    // ---- Filename -----------------------------------------------------------------------------

    [Theory]
    [InlineData("people", "csv", "people.csv")]
    [InlineData("customers/people", "csv", "customers_people.csv")]
    [InlineData("people", "markdown", "people.md")]
    [InlineData(null, "json", "export.json")]
    public void DefaultFilename_sanitizes_base_and_maps_extension(string? baseName, string format, string expected)
        => Assert.Equal(expected, CrudExport.DefaultFilename(baseName, format));
}
