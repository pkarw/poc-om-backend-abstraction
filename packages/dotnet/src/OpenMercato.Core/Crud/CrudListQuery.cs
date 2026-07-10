using Microsoft.AspNetCore.Http;

namespace OpenMercato.Core.Crud;

/// <summary>
/// Parsed standard CRUD list query parameters — the reusable port of upstream's list query-param
/// handling (packages/shared/src/lib/crud/factory.ts list branch + ids.ts). Semantics that leak to the
/// wire are reproduced exactly (spec 02 R20–R24):
///   - <see cref="Page"/> default 1
///   - <see cref="PageSize"/> default 50, clamped to [1, <c>maxPageSize</c>]
///   - sort field from <c>sortField ?? sort ?? default</c>; direction from <c>sortDir ?? order</c>
///     (<c>desc</c> matched case-insensitively), default asc
///   - <see cref="Ids"/> comma-separated UUIDs: trimmed, deduped, non-UUIDs dropped, capped at 200
///   - <see cref="WithDeleted"/> boolean token
///   - unknown params survive into <see cref="Filters"/> (passthrough for dynamic <c>cf_*</c> filters)
/// </summary>
public sealed record CrudListQuery
{
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required string SortField { get; init; }
    public required bool SortDescending { get; init; }
    public string? Search { get; init; }
    public required IReadOnlyList<Guid> Ids { get; init; }
    public required bool WithDeleted { get; init; }

    /// <summary>The single-item selector (<c>?id=</c>) when present and a valid UUID.</summary>
    public Guid? SingleId { get; init; }

    /// <summary>Remaining query params (arbitrary filter fields incl. dynamic <c>cf_*</c>).</summary>
    public required IReadOnlyDictionary<string, string> Filters { get; init; }

    /// <summary>
    /// Optional relational restriction resolved by a route (upstream <c>applyEntityIdRestriction</c>): when
    /// non-null the list is limited to these record ids (an empty list means "no matches"). Used for
    /// association filters like the deals list <c>?personId=</c>/<c>?companyId=</c> that are NOT index doc
    /// fields — the route resolves the linked record ids and sets this.
    /// </summary>
    public IReadOnlyList<Guid>? RestrictIds { get; init; }
}

/// <summary>Reusable parsing + envelope helpers shared by every CRUD list endpoint.</summary>
public static class CrudListQueryParser
{
    public const int MaxIdsPerRequest = 200;

    private static readonly HashSet<string> KnownParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "sortField", "sort", "sortDir", "order", "search",
        "ids", "id", "withDeleted", "format", "exportScope", "export_scope", "full",
    };

    /// <summary>Parse the standard list query params from an incoming request.</summary>
    public static CrudListQuery Parse(HttpRequest request, string defaultSortField, int defaultPageSize, int maxPageSize)
    {
        var q = request.Query;

        var page = ToInt(q["page"], 1);
        if (page < 1) page = 1;

        var pageSize = ToInt(q["pageSize"], defaultPageSize);
        pageSize = Math.Min(Math.Max(pageSize, 1), Math.Max(1, maxPageSize));

        var sortField = FirstNonEmpty(q["sortField"], q["sort"]) ?? defaultSortField;
        if (string.IsNullOrEmpty(sortField)) sortField = defaultSortField;
        var sortDir = FirstNonEmpty(q["sortDir"], q["order"]) ?? "asc";
        var sortDescending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        var search = FirstNonEmpty(q["search"]);
        var ids = ParseIds(q["ids"].ToString());
        var withDeleted = ParseBooleanToken(q["withDeleted"].ToString());

        Guid? singleId = null;
        var rawId = q["id"].ToString();
        if (!string.IsNullOrWhiteSpace(rawId) && Guid.TryParse(rawId.Trim(), out var parsedId))
            singleId = parsedId;

        var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in q)
        {
            if (KnownParams.Contains(kv.Key)) continue;
            var value = kv.Value.ToString();
            if (!string.IsNullOrEmpty(value)) filters[kv.Key] = value;
        }

        return new CrudListQuery
        {
            Page = page,
            PageSize = pageSize,
            SortField = sortField,
            SortDescending = sortDescending,
            Search = search,
            Ids = ids,
            WithDeleted = withDeleted,
            SingleId = singleId,
            Filters = filters,
        };
    }

    /// <summary>
    /// Parse a comma-separated <c>?ids=</c> value (port of upstream <c>parseIdsParam</c>): trim, drop
    /// non-UUIDs, dedupe, cap at <see cref="MaxIdsPerRequest"/>.
    /// </summary>
    public static IReadOnlyList<Guid> ParseIds(string? raw, int maxIds = MaxIdsPerRequest)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Guid>();
        var cap = maxIds > 0 ? maxIds : MaxIdsPerRequest;
        var seen = new HashSet<Guid>();
        var result = new List<Guid>();
        foreach (var part in raw.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (!Guid.TryParse(trimmed, out var g)) continue;
            if (seen.Add(g))
            {
                result.Add(g);
                if (result.Count >= cap) break;
            }
        }
        return result;
    }

    /// <summary>Parse a boolean token (only <c>true/1/yes/on</c>, case-insensitive, are true).</summary>
    public static bool ParseBooleanToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    /// <summary>
    /// Build the canonical list envelope <c>{ items, total, page, pageSize, totalPages }</c>
    /// (spec 02 R19). <c>totalPages = ceil(total / pageSize)</c> with a guard against a 0 page size.
    /// </summary>
    public static object BuildEnvelope(IReadOnlyList<object> items, int total, int page, int pageSize)
    {
        var effectivePageSize = pageSize > 0 ? pageSize : 1;
        var totalPages = (int)Math.Ceiling(total / (double)effectivePageSize);
        return new
        {
            items,
            total,
            page,
            pageSize,
            totalPages,
        };
    }

    private static int ToInt(Microsoft.Extensions.Primitives.StringValues value, int fallback)
    {
        var raw = value.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return int.TryParse(raw.Trim(), out var parsed) ? parsed : fallback;
    }

    private static string? FirstNonEmpty(params Microsoft.Extensions.Primitives.StringValues[] values)
    {
        foreach (var v in values)
        {
            var s = v.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }
}
