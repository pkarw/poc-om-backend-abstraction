using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Price-kinds CRUD — the port of upstream <c>api/price-kinds/route.ts</c>. Price kinds are tenant-scoped
/// (organization_id is nullable / tenant-global), so the list is NOT org-scoped. All methods require
/// <c>catalog.settings.manage</c>. Index-backed list; dispatch to <c>catalog.priceKinds.*</c>.
/// </summary>
public sealed class PriceKindsRoutes : ICatalogRouteGroup
{
    private static readonly string[] Manage = { "catalog.settings.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogPriceKind> Config() => new()
    {
        BasePath = "catalog/price-kinds",
        EntityType = CatalogIndexEntity.PriceKind,
        ResourceKind = "catalog.price_kind",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        OrgScoped = false, // tenant-global; organization_id is nullable (upstream orgField: null)
        ListFeatures = Manage,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = k => k.Id,
        DeletedAtSelector = k => k.DeletedAt,
        TenantIdSelector = k => k.TenantId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogPriceKind>, bool, IOrderedQueryable<CatalogPriceKind>>>
        {
            ["code"] = (q, d) => d ? q.OrderByDescending(k => k.Code) : q.OrderBy(k => k.Code),
            ["title"] = (q, d) => d ? q.OrderByDescending(k => k.Title) : q.OrderBy(k => k.Title),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(k => k.CreatedAt) : q.OrderBy(k => k.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(k => k.UpdatedAt) : q.OrderBy(k => k.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("isPromotion", out var ip) && CatalogFilter.TryBool(ip, out var promo))
                q = q.Where(k => k.IsPromotion == promo);
            if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
                q = q.Where(k => k.IsActive == active);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                q = q.Where(k => k.Code.ToLower().Contains(term) || k.Title.ToLower().Contains(term));
            }
            return q;
        },
        ProjectItem = k => new Dictionary<string, object?>(CatalogIndexBaseRowResolver.ProjectPriceKindDoc(k)),
        CreatedEvent = null,
        UpdatedEvent = null,
        DeletedEvent = null,
        ValidateCreate = Data.CatalogValidators.PriceKind,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<PriceKindCreateInput, PriceKindResult>(
                "catalog.priceKinds.create",
                new PriceKindCreateInput(m.Ctx.OrganizationId, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceKindId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<PriceKindUpdateInput, PriceKindResult>(
                "catalog.priceKinds.update", new PriceKindUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceKindId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Price kind id is required");
            var r = await m.Bus.ExecuteWithLog<PriceKindDeleteInput, PriceKindResult>(
                "catalog.priceKinds.delete", new PriceKindDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceKindId, r.LogEntry);
        },
    };
}
