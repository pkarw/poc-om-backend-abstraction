using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex.Crud;

/// <summary>
/// The real <see cref="ICrudIndexQuery"/> — adapts the CRUD factory's <see cref="CrudListQuery"/>
/// (arbitrary <c>cf_*</c> / base filters, free-text <c>search</c>, sort, paging, tenant/org scope) onto
/// the <see cref="IQueryIndexEngine"/> so an opt-in list (<c>CrudConfig.UseIndexList = true</c>) filters
/// and sorts by custom fields against the <c>entity_indexes</c> read model. E.g. the customers
/// people/companies list resolves <c>?cf_tier=gold&amp;sortField=cf_score</c> here.
/// </summary>
public sealed class QueryIndexCrudListQuery : ICrudIndexQuery
{
    private readonly IQueryIndexEngine _engine;
    public QueryIndexCrudListQuery(IQueryIndexEngine engine) => _engine = engine;

    public async Task<CrudIndexQueryResult?> ResolveListAsync(
        string entityType, CrudListQuery query, CommandContext ctx, CancellationToken ct = default)
    {
        // Tenant is required by the index (upstream: QueryEngine throws without tenantId).
        if (ctx.TenantId is null) return null;

        var filters = new List<IndexFilter>();
        foreach (var (field, value) in query.Filters)
            filters.Add(new IndexFilter(field, IndexFilterOp.Eq, value));

        var sorts = new List<IndexSort> { new(query.SortField, query.SortDescending) };

        var request = new QueryIndexRequest
        {
            EntityType = entityType,
            TenantId = ctx.TenantId,
            OrganizationIds = ctx.OrganizationIds,
            Filters = filters,
            Sort = sorts,
            Page = query.Page,
            PageSize = query.PageSize,
            WithDeleted = query.WithDeleted,
            // Free-text search resolves via search_tokens (AND-of-hashes), with an ilike-on-search_text
            // fallback inside the engine when the scope has no tokens.
            FullTextSearch = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search,
        };

        var result = await _engine.QueryAsync(request, ct);

        var ids = new List<Guid>(result.RecordIds.Count);
        foreach (var raw in result.RecordIds)
            if (Guid.TryParse(raw, out var g)) ids.Add(g);

        return new CrudIndexQueryResult(ids, result.Total);
    }
}
