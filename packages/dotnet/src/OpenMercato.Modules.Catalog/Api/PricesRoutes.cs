using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Prices CRUD — the port of upstream <c>api/prices/route.ts</c>. Index-backed list with the full scope
/// filter set (product/variant/offer/channel/currency/priceKind/kind + user/customer scopes). GET
/// requires <c>catalog.products.view</c>; mutations require <c>catalog.pricing.manage</c>. The
/// <c>catalog_product_variant_prices</c> table has NO soft-delete (delete hard-removes the row).
///
/// PARITY-TODO: the upstream quantity-normalization afterList filter (unit-conversion aware
/// min/max_quantity narrowing) is deferred with the pricing/unit-conversion port.
/// </summary>
public sealed class PricesRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };
    private static readonly string[] Manage = { "catalog.pricing.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogProductPrice> Config() => new()
    {
        BasePath = "catalog/prices",
        EntityType = CatalogIndexEntity.Price,
        ResourceKind = "catalog.price",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        SoftDelete = false, // no deleted_at column on catalog_product_variant_prices
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = p => p.Id,
        TenantIdSelector = p => p.TenantId,
        OrganizationIdSelector = p => p.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogProductPrice>, bool, IOrderedQueryable<CatalogProductPrice>>>
        {
            ["currencyCode"] = (q, d) => d ? q.OrderByDescending(p => p.CurrencyCode) : q.OrderBy(p => p.CurrencyCode),
            ["kind"] = (q, d) => d ? q.OrderByDescending(p => p.Kind) : q.OrderBy(p => p.Kind),
            ["minQuantity"] = (q, d) => d ? q.OrderByDescending(p => p.MinQuantity) : q.OrderBy(p => p.MinQuantity),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("productId", out var pid) && Guid.TryParse(pid, out var productId))
                q = q.Where(p => p.ProductId == productId);
            if (query.Filters.TryGetValue("variantId", out var vid) && Guid.TryParse(vid, out var variantId))
                q = q.Where(p => p.VariantId == variantId);
            if (query.Filters.TryGetValue("offerId", out var oid) && Guid.TryParse(oid, out var offerId))
                q = q.Where(p => p.OfferId == offerId);
            if (query.Filters.TryGetValue("channelId", out var cid) && Guid.TryParse(cid, out var channelId))
                q = q.Where(p => p.ChannelId == channelId);
            if (query.Filters.TryGetValue("priceKindId", out var pk) && Guid.TryParse(pk, out var priceKindId))
                q = q.Where(p => p.PriceKindId == priceKindId);
            if (query.Filters.TryGetValue("currencyCode", out var cur) && !string.IsNullOrWhiteSpace(cur))
            {
                var code = cur.Trim().ToUpperInvariant();
                q = q.Where(p => p.CurrencyCode == code);
            }
            if (query.Filters.TryGetValue("kind", out var kind) && !string.IsNullOrWhiteSpace(kind))
                q = q.Where(p => p.Kind == kind);
            if (query.Filters.TryGetValue("userId", out var uid) && Guid.TryParse(uid, out var userId))
                q = q.Where(p => p.UserId == userId);
            if (query.Filters.TryGetValue("userGroupId", out var ugid) && Guid.TryParse(ugid, out var userGroupId))
                q = q.Where(p => p.UserGroupId == userGroupId);
            if (query.Filters.TryGetValue("customerId", out var custId) && Guid.TryParse(custId, out var customerId))
                q = q.Where(p => p.CustomerId == customerId);
            if (query.Filters.TryGetValue("customerGroupId", out var cgid) && Guid.TryParse(cgid, out var customerGroupId))
                q = q.Where(p => p.CustomerGroupId == customerGroupId);
            return q;
        },
        // Quantity-normalization context params (deferred) are not price doc fields.
        NonFilterParams = new[] { "quantity", "quantityUnit" },
        ProjectItem = p => new Dictionary<string, object?>(CatalogIndexBaseRowResolver.ProjectPriceDoc(p)),
        CreatedEvent = "catalog.price.created",
        UpdatedEvent = "catalog.price.updated",
        DeletedEvent = "catalog.price.deleted",
        ValidateCreate = Data.CatalogValidators.Price,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<PriceCreateInput, PriceResult>(
                "catalog.prices.create",
                new PriceCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<PriceUpdateInput, PriceResult>(
                "catalog.prices.update", new PriceUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Price id is required");
            var r = await m.Bus.ExecuteWithLog<PriceDeleteInput, PriceResult>(
                "catalog.prices.delete", new PriceDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.PriceId, r.LogEntry);
        },
    };
}
