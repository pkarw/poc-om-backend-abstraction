using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.QueryIndex.Data;

namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>Filter operators supported by the query engine (upstream <c>FilterOp</c>).</summary>
public enum IndexFilterOp { Eq, Ne, In, Nin, Like, Ilike, Exists, Gt, Gte, Lt, Lte }

/// <summary>One filter over a doc field. <see cref="Field"/> may be a base column, <c>cf:&lt;key&gt;</c>, or <c>cf_&lt;key&gt;</c> (normalized).</summary>
public sealed record IndexFilter(string Field, IndexFilterOp Op, object? Value);

/// <summary>One sort directive over a doc field.</summary>
public sealed record IndexSort(string Field, bool Descending);

/// <summary>A list query against the <c>entity_indexes</c> projection.</summary>
public sealed record QueryIndexRequest
{
    public required string EntityType { get; init; }
    public required Guid? TenantId { get; init; }
    /// <summary>Org scope: null = no org filter; non-empty = <c>organization_id in ids OR is null</c>.</summary>
    public IReadOnlyList<Guid>? OrganizationIds { get; init; }
    public IReadOnlyList<IndexFilter> Filters { get; init; } = Array.Empty<IndexFilter>();
    public IReadOnlyList<IndexSort> Sort { get; init; } = Array.Empty<IndexSort>();
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public bool WithDeleted { get; init; }
    /// <summary>Raw free-text query. When set, resolved via <c>search_tokens</c> (AND-of-hashes) with an
    /// ilike-on-<see cref="SearchField"/> fallback when the scope has no tokens (upstream applySearchTokens).</summary>
    public string? FullTextSearch { get; init; }
    /// <summary>The doc/token field the free-text search targets (upstream aggregate <c>search_text</c>).</summary>
    public string SearchField { get; init; } = IndexDocument.AggregateSearchField;
}

/// <summary>Result of a query: the page of matching record ids (in sort order) + the total match count.</summary>
public sealed record QueryIndexResult(IReadOnlyList<string> RecordIds, int Total);

/// <summary>
/// The query-index read engine — the port of upstream <c>lib/engine.ts</c>'s <c>entity_indexes</c> list
/// path. Given an entity type + filters (incl. <c>cf:&lt;key&gt;</c>) + sort + paging + scope, it returns
/// matching record ids and the total from the projection. This is what an index-backed CRUD list uses
/// to filter/sort by custom fields (e.g. customers people/companies on <c>cf_*</c>).
/// </summary>
public interface IQueryIndexEngine
{
    Task<QueryIndexResult> QueryAsync(QueryIndexRequest request, CancellationToken ct = default);
}

/// <summary>
/// EF-backed engine. Scoping (entity type / tenant / org / soft-delete) runs as SQL (LINQ, portable
/// across the in-memory test provider and Postgres); the doc filter/sort evaluation runs in memory over
/// the parsed <c>doc</c> so the exact JSON semantics (base + <c>cf:&lt;key&gt;</c>, array-contains for cf
/// <c>eq</c>/<c>in</c>, text comparisons) are reproduced without provider-specific jsonb SQL.
/// PARITY-TODO: native <c>doc ->></c> / <c>@></c> SQL pushdown + the coalesced-org partial indexes.
/// </summary>
public sealed class QueryIndexEngine : IQueryIndexEngine
{
    private readonly AppDbContext _db;
    public QueryIndexEngine(AppDbContext db) => _db = db;

    public async Task<QueryIndexResult> QueryAsync(QueryIndexRequest request, CancellationToken ct = default)
    {
        var q = _db.Set<EntityIndexRow>().AsNoTracking()
            .Where(r => r.EntityType == request.EntityType);

        // Upstream requires a tenant; scope by exact tenant match.
        q = q.Where(r => r.TenantId == request.TenantId);

        if (!request.WithDeleted) q = q.Where(r => r.DeletedAt == null);

        // Org scope (applyOrganizationScope): in(ids) OR is null. Null scope → no org filter.
        if (request.OrganizationIds is { Count: > 0 } orgIds)
            q = q.Where(r => r.OrganizationId == null || orgIds.Contains(r.OrganizationId.Value));

        // Resolve free-text search up front: prefer the tokenized search_tokens path (AND-of-hashes),
        // fall back to an in-memory ilike on the aggregate field when the scope has no tokens (or the
        // provider can't run the token query). This runs before row load so it can constrain by id.
        var filters = request.Filters.Select(NormalizeFilter).ToList();
        HashSet<string>? tokenIds = null; // non-null ⇒ restrict results to these entity ids (token path)
        if (!string.IsNullOrWhiteSpace(request.FullTextSearch))
        {
            var (mode, matched) = await ResolveSearchAsync(request, ct);
            switch (mode)
            {
                case SearchMode.Token:
                    tokenIds = matched;
                    break;
                case SearchMode.TokenMatchAll:
                    // Query shorter than minTokenLength ⇒ no token constraint (upstream returns unfiltered).
                    break;
                case SearchMode.Fallback:
                    filters.Add(NormalizeFilter(new IndexFilter(
                        request.SearchField, IndexFilterOp.Ilike, "%" + request.FullTextSearch + "%")));
                    break;
            }
        }

        var rows = await q.ToListAsync(ct);

        // Parse docs + apply filters in memory.
        var evaluated = new List<(string RecordId, Dictionary<string, object?> Doc)>(rows.Count);
        foreach (var row in rows)
        {
            if (tokenIds is not null && !tokenIds.Contains(row.EntityId)) continue;
            var doc = DocJson.ParseObject(row.Doc);
            if (filters.All(f => Matches(doc, f)))
                evaluated.Add((row.EntityId, doc));
        }

        var total = evaluated.Count;

        // Sorting (text order per field; nulls last on asc / first on desc, matching Postgres defaults).
        IEnumerable<(string RecordId, Dictionary<string, object?> Doc)> ordered = evaluated;
        var sorts = request.Sort;
        for (var i = sorts.Count - 1; i >= 0; i--)
        {
            var s = sorts[i];
            var key = NormalizeField(s.Field);
            ordered = ApplySort(ordered, key, s.Descending);
        }

        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 20 : request.PageSize;
        var ids = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.RecordId)
            .ToList();

        return new QueryIndexResult(ids, total);
    }

    private enum SearchMode { Token, TokenMatchAll, Fallback }

    /// <summary>
    /// The read-side port of engine.ts::applySearchTokens. If the (entity_type, field, scope) has any
    /// <c>search_tokens</c> rows: tokenize the query and return the entity ids that carry <em>all</em> of
    /// the query's token hashes (AND-of-tokens). If the scope has no tokens — or the token query can't run
    /// on this provider — signal a literal ilike fallback so nothing regresses when tokens are absent.
    /// </summary>
    private async Task<(SearchMode Mode, HashSet<string> Ids)> ResolveSearchAsync(
        QueryIndexRequest request, CancellationToken ct)
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var field = request.SearchField;
            var scoped = _db.Set<SearchToken>().AsNoTracking()
                .Where(t => t.EntityType == request.EntityType && t.Field == field)
                .Where(t => t.TenantId == request.TenantId);
            if (request.OrganizationIds is { Count: > 0 } orgIds)
                scoped = scoped.Where(t => t.OrganizationId == null || orgIds.Contains(t.OrganizationId.Value));

            // No tokens for this scope ⇒ fall back to literal ilike (protects legacy/un-tokenized data).
            if (!await scoped.AnyAsync(ct))
                return (SearchMode.Fallback, empty);

            var result = SearchTokenizer.Tokenize(request.FullTextSearch!, SearchConfig.Resolve());
            var hashes = result.Hashes;
            if (hashes.Count == 0)
                return (SearchMode.TokenMatchAll, empty);

            // entity ids carrying all query hashes: group (entity_id, hash) pairs, keep those with the full set.
            var pairs = await scoped
                .Where(t => hashes.Contains(t.TokenHash))
                .Select(t => new { t.EntityId, t.TokenHash })
                .Distinct()
                .ToListAsync(ct);

            var matched = pairs
                .GroupBy(p => p.EntityId, StringComparer.Ordinal)
                .Where(g => g.Select(x => x.TokenHash).Distinct().Count() >= hashes.Count)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal);
            return (SearchMode.Token, matched);
        }
        catch
        {
            // Provider can't translate the token query (or transient error) ⇒ literal ilike fallback.
            return (SearchMode.Fallback, empty);
        }
    }

    private static IEnumerable<(string RecordId, Dictionary<string, object?> Doc)> ApplySort(
        IEnumerable<(string RecordId, Dictionary<string, object?> Doc)> source, string key, bool descending)
    {
        // Stable ordering by the field's text value; nulls sort last for asc, first for desc.
        var withText = source.Select(x => (x, text: DocJson.ToText(SortValue(x.Doc, key)))).ToList();
        IEnumerable<((string, Dictionary<string, object?>) x, string? text)> result = descending
            ? withText.OrderByDescending(e => e.text is null ? 0 : 1).ThenByDescending(e => e.text, StringComparer.Ordinal)
            : withText.OrderBy(e => e.text is null ? 1 : 0).ThenBy(e => e.text, StringComparer.Ordinal);
        return result.Select(e => e.x);
    }

    private static object? SortValue(Dictionary<string, object?> doc, string key)
    {
        if (key == "id") return doc.TryGetValue("id", out var id) ? id : null;
        return doc.TryGetValue(key, out var v) ? v : null;
    }

    private IndexFilter NormalizeFilter(IndexFilter f) => f with { Field = NormalizeField(f.Field) };

    private static string NormalizeField(string field)
        => field.StartsWith("cf_", StringComparison.Ordinal) ? "cf:" + field[3..] : field;

    private static bool Matches(Dictionary<string, object?> doc, IndexFilter filter)
    {
        doc.TryGetValue(filter.Field, out var raw);
        var text = DocJson.ToText(raw);
        var isCf = filter.Field.StartsWith("cf:", StringComparison.Ordinal);

        switch (filter.Op)
        {
            case IndexFilterOp.Eq:
                return TextEquals(text, filter.Value) || (isCf && ArrayContains(raw, filter.Value));
            case IndexFilterOp.Ne:
                return !TextEquals(text, filter.Value);
            case IndexFilterOp.In:
                return ToArray(filter.Value).Any(v => TextEquals(text, v) || (isCf && ArrayContains(raw, v)));
            case IndexFilterOp.Nin:
                return !ToArray(filter.Value).Any(v => TextEquals(text, v));
            case IndexFilterOp.Like:
                return text is not null && Like(text, ValueText(filter.Value), caseInsensitive: false);
            case IndexFilterOp.Ilike:
                return text is not null && Like(text, ValueText(filter.Value), caseInsensitive: true);
            case IndexFilterOp.Exists:
                return Truthy(filter.Value) ? text is not null : text is null;
            case IndexFilterOp.Gt: return Compare(text, filter.Value) > 0;
            case IndexFilterOp.Gte: return Compare(text, filter.Value) >= 0;
            case IndexFilterOp.Lt: return Compare(text, filter.Value) < 0;
            case IndexFilterOp.Lte: return Compare(text, filter.Value) <= 0;
            default: return true;
        }
    }

    private static bool TextEquals(string? text, object? value)
        => text is not null && string.Equals(text, ValueText(value), StringComparison.Ordinal);

    private static bool ArrayContains(object? raw, object? value)
    {
        if (raw is System.Collections.IEnumerable arr and not string)
            return arr.Cast<object?>().Any(e => string.Equals(DocJson.ToText(e), ValueText(value), StringComparison.Ordinal));
        return false;
    }

    private static int Compare(string? text, object? value)
    {
        // Upstream compares (doc ->> key) as text against the value. Reproduce text (ordinal) comparison.
        if (text is null) return -1;
        return string.CompareOrdinal(text, ValueText(value) ?? string.Empty);
    }

    private static bool Like(string text, string? pattern, bool caseInsensitive)
    {
        if (pattern is null) return false;
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("%", ".*").Replace("_", ".") + "$";
        var opts = System.Text.RegularExpressions.RegexOptions.Singleline;
        if (caseInsensitive) opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        return System.Text.RegularExpressions.Regex.IsMatch(text, regex, opts);
    }

    private static string? ValueText(object? value) => DocJson.ToText(value);

    private static IReadOnlyList<object?> ToArray(object? value)
    {
        if (value is System.Collections.IEnumerable e and not string)
            return e.Cast<object?>().ToList();
        return new[] { value };
    }

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s is not ("false" or "0" or "" or "no" or "off"),
        _ => true,
    };
}
