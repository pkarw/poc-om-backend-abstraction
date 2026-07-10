namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>One inverted-index row to write (upstream <c>SearchTokenRow</c>, field + token_hash + raw token).</summary>
public sealed record SearchTokenRowData(string Field, string TokenHash, string? Token);

/// <summary>
/// The port of upstream <c>packages/core/src/modules/query_index/lib/search-tokens.ts</c>
/// (<c>buildSearchTokenRows</c> / <c>shouldIndexField</c>): tokenizes the indexable string/array fields of
/// a (decrypted) index doc into <c>search_tokens</c> rows. Skips <c>id</c>/<c>*_id</c>/<c>*.id</c>/<c>*_at</c>,
/// the timestamp/scope columns, and blocklisted fields; dedupes rows per <c>field|hash</c>.
/// </summary>
public static class SearchTokenRowBuilder
{
    private static readonly string[] ExcludedExact = { "created_at", "updated_at", "deleted_at", "tenant_id", "organization_id" };

    /// <summary>Field-level filter — search-tokens.ts::shouldIndexField.</summary>
    private static bool ShouldIndexField(string field, object? value, SearchConfig config)
    {
        if (value is not string && value is not System.Collections.IEnumerable) return false;
        var lower = field.ToLowerInvariant();
        if (lower == "id" || lower.EndsWith("_id", StringComparison.Ordinal) || lower.EndsWith(".id", StringComparison.Ordinal)) return false;
        if (lower.EndsWith("_at", StringComparison.Ordinal)) return false;
        if (ExcludedExact.Contains(lower)) return false;
        if (config.BlocklistedFields.Any(blocked => lower.Contains(blocked, StringComparison.Ordinal))) return false;
        var values = CollectTextValues(value);
        if (values.Count == 0) return false;
        return values.Any(text => SearchTokenizer.Tokenize(text, config).Tokens.Count > 0);
    }

    // search-tokens.ts::collectTextValues — string ⇒ [string]; array ⇒ string entries only; else [].
    private static List<string> CollectTextValues(object? value)
    {
        if (value is string s) return new List<string> { s };
        if (value is System.Collections.IEnumerable arr and not string)
        {
            var outp = new List<string>();
            foreach (var entry in arr)
                if (entry is string es) outp.Add(es);
            return outp;
        }
        return new List<string>();
    }

    /// <summary>Build the token rows for one record's doc (search-tokens.ts::buildSearchTokenRows).</summary>
    public static List<SearchTokenRowData> Build(IReadOnlyDictionary<string, object?> doc, SearchConfig config)
    {
        var rows = new List<SearchTokenRowData>();
        if (!config.Enabled) return rows;

        foreach (var (field, rawValue) in doc)
        {
            if (!ShouldIndexField(field, rawValue, config)) continue;
            var values = CollectTextValues(rawValue);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var text in values)
            {
                var result = SearchTokenizer.Tokenize(text, config);
                for (var i = 0; i < result.Tokens.Count; i++)
                {
                    var token = result.Tokens[i];
                    var hash = result.Hashes[i];
                    var dedupeKey = field + "|" + hash;
                    if (!seen.Add(dedupeKey)) continue;
                    rows.Add(new SearchTokenRowData(field, hash, config.StoreRawTokens ? token : null));
                }
            }
        }
        return rows;
    }

    /// <summary>The set of doc field keys whose tokens get replaced on write (search-tokens.ts::buildFieldPairs).</summary>
    public static IReadOnlyList<string> DocFields(IReadOnlyDictionary<string, object?> doc)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<string>();
        foreach (var field in doc.Keys)
            if (seen.Add(field)) fields.Add(field);
        return fields;
    }
}
