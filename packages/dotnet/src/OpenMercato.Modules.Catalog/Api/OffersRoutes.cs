using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Offers CRUD — the port of upstream <c>api/offers/route.ts</c>. Index-backed list, but the list item
/// is <b>camelCase</b> (the OM offers UI shape) rather than the snake_case DataQuery shape of the other
/// catalog lists. All methods require <c>sales.channels.manage</c> (a sales feature — offers live at the
/// catalog/sales boundary). The <c>afterList</c> hook attaches the offer's <c>product</c> summary, its
/// <c>prices</c> (with price-kind code/title/displayMode), and the product-level fallback pricing
/// (<c>productChannelPrice</c> / <c>productDefaultPrices</c> — the channel-priority resolution over
/// offer-less product/variant prices).
/// </summary>
public sealed class OffersRoutes : ICatalogRouteGroup
{
    private static readonly string[] Manage = { "sales.channels.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogOffer> Config() => new()
    {
        BasePath = "catalog/offers",
        EntityType = CatalogIndexEntity.Offer,
        ResourceKind = "catalog.offer",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        ListFeatures = Manage,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = o => o.Id,
        DeletedAtSelector = o => o.DeletedAt,
        TenantIdSelector = o => o.TenantId,
        OrganizationIdSelector = o => o.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogOffer>, bool, IOrderedQueryable<CatalogOffer>>>
        {
            ["title"] = (q, d) => d ? q.OrderByDescending(o => o.Title) : q.OrderBy(o => o.Title),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(o => o.CreatedAt) : q.OrderBy(o => o.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(o => o.UpdatedAt) : q.OrderBy(o => o.UpdatedAt),
        },
        ApplyFilters = ApplyFilters,
        ProjectItem = ProjectListItem,
        ListHook = DecorateAsync,
        CreatedEvent = null,
        UpdatedEvent = null,
        DeletedEvent = null,
        ValidateCreate = Data.CatalogValidators.Offer,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<OfferCreateInput, OfferResult>(
                "catalog.offers.create",
                new OfferCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.OfferId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<OfferUpdateInput, OfferResult>(
                "catalog.offers.update", new OfferUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.OfferId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Offer id is required");
            var r = await m.Bus.ExecuteWithLog<OfferDeleteInput, OfferResult>(
                "catalog.offers.delete", new OfferDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.OfferId, r.LogEntry);
        },
    };

    private static IQueryable<CatalogOffer> ApplyFilters(IQueryable<CatalogOffer> q, CrudListQuery query, CommandContext ctx)
    {
        if (query.Filters.TryGetValue("productId", out var pid) && Guid.TryParse(pid, out var productId))
            q = q.Where(o => o.ProductId == productId);
        if (query.Filters.TryGetValue("channelId", out var cid) && Guid.TryParse(cid, out var channelId))
            q = q.Where(o => o.ChannelId == channelId);
        else if (query.Filters.TryGetValue("channelIds", out var cids) && !string.IsNullOrWhiteSpace(cids))
        {
            var ids = cids.Split(',').Select(s => Guid.TryParse(s.Trim(), out var g) ? g : (Guid?)null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();
            if (ids.Count > 0) q = q.Where(o => ids.Contains(o.ChannelId));
        }
        if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
            q = q.Where(o => o.IsActive == active);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(o =>
                o.Title.ToLower().Contains(term) ||
                (o.Description != null && o.Description.ToLower().Contains(term)));
        }
        return q;
    }

    /// <summary>Offer list item in the OM camelCase shape (offer route <c>transformItem</c>).</summary>
    internal static IDictionary<string, object?> ProjectListItem(CatalogOffer o) => new Dictionary<string, object?>
    {
        ["id"] = o.Id.ToString(),
        ["productId"] = o.ProductId.ToString(),
        ["organizationId"] = o.OrganizationId.ToString(),
        ["tenantId"] = o.TenantId.ToString(),
        ["channelId"] = o.ChannelId.ToString(),
        ["title"] = o.Title,
        ["description"] = o.Description,
        ["defaultMediaId"] = o.DefaultMediaId?.ToString(),
        ["defaultMediaUrl"] = o.DefaultMediaUrl,
        ["metadata"] = CatalogHttp.JsonValue(o.Metadata),
        ["isActive"] = o.IsActive,
        ["createdAt"] = CatalogHttp.Iso(o.CreatedAt),
        ["updatedAt"] = CatalogHttp.Iso(o.UpdatedAt),
    };

    private static async Task DecorateAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var tenantId = ctx.TenantId;

        var offerIds = items.Select(i => Guid.TryParse(i.TryGetValue("id", out var v) ? v?.ToString() : null, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        var productIds = items.Select(i => Guid.TryParse(i.TryGetValue("productId", out var v) ? v?.ToString() : null, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();

        // Product summary per offer.
        var products = productIds.Count == 0 ? new List<CatalogProduct>()
            : await db.Set<CatalogProduct>().AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && (tenantId == null || p.TenantId == tenantId)).ToListAsync();
        var productById = products.ToDictionary(p => p.Id.ToString(), p => (object)new Dictionary<string, object?>
        {
            ["id"] = p.Id.ToString(),
            ["title"] = p.Title,
            ["defaultMediaId"] = p.DefaultMediaId?.ToString(),
            ["defaultMediaUrl"] = p.DefaultMediaUrl,
            ["sku"] = p.Sku,
        }, StringComparer.Ordinal);

        // Prices belonging to these offers (+ their price-kind info).
        var priceRows = offerIds.Count == 0 ? new List<CatalogProductPrice>()
            : await db.Set<CatalogProductPrice>().AsNoTracking()
                .Where(pr => pr.OfferId != null && offerIds.Contains(pr.OfferId.Value) && (tenantId == null || pr.TenantId == tenantId))
                .ToListAsync();
        var kindIds = priceRows.Select(pr => pr.PriceKindId).Distinct().ToList();
        var kinds = kindIds.Count == 0 ? new List<CatalogPriceKind>()
            : await db.Set<CatalogPriceKind>().AsNoTracking().Where(k => kindIds.Contains(k.Id)).ToListAsync();
        var kindById = kinds.ToDictionary(k => k.Id);
        var pricesByOffer = new Dictionary<Guid, List<object>>();
        foreach (var pr in priceRows)
        {
            if (pr.OfferId is not { } offerId) continue;
            kindById.TryGetValue(pr.PriceKindId, out var kind);
            var bucket = pricesByOffer.TryGetValue(offerId, out var b) ? b : pricesByOffer[offerId] = new List<object>();
            bucket.Add(new Dictionary<string, object?>
            {
                ["id"] = pr.Id.ToString(),
                ["priceKindId"] = pr.PriceKindId.ToString(),
                ["priceKindCode"] = kind?.Code,
                ["priceKindTitle"] = kind?.Title,
                ["currencyCode"] = pr.CurrencyCode,
                ["unitPriceNet"] = pr.UnitPriceNet,
                ["unitPriceGross"] = pr.UnitPriceGross,
                ["displayMode"] = kind?.DisplayMode ?? "excluding-tax",
                ["minQuantity"] = pr.MinQuantity,
                ["maxQuantity"] = pr.MaxQuantity,
            });
        }

        // Fallback product-channel pricing: offer-less prices at product OR default-variant level, bucketed
        // per (product, channel) with a priority (variant>product, channel-specific>default). Higher
        // priority wins per bucket; equal-priority rows accumulate (upstream assignFallbackPrice).
        var fallbackByProduct = await BuildFallbackPricingAsync(db, tenantId, productIds, items, kindById);

        foreach (var item in items)
        {
            var pid = item.TryGetValue("productId", out var pv) ? pv?.ToString() : null;
            item["product"] = pid is not null && productById.TryGetValue(pid, out var prod) ? prod : null;
            var oid = Guid.TryParse(item.TryGetValue("id", out var iv) ? iv?.ToString() : null, out var g) ? g : Guid.Empty;
            item["prices"] = pricesByOffer.TryGetValue(oid, out var pr) ? pr : new List<object>();

            List<object> effective = new();
            if (Guid.TryParse(pid, out var productGuid) && fallbackByProduct.TryGetValue(productGuid, out var bucket))
            {
                var channelKey = item.TryGetValue("channelId", out var cv) && cv is string cs && cs.Length > 0 ? cs : DefaultChannelKey;
                if (bucket.TryGetValue(channelKey, out var chGroup)) effective = chGroup.Prices;
                else if (bucket.TryGetValue(DefaultChannelKey, out var defGroup)) effective = defGroup.Prices;
            }
            item["productChannelPrice"] = effective.Count > 0 ? effective[0] : null;
            item["productDefaultPrices"] = effective;
        }
    }

    private const string DefaultChannelKey = "__default__";

    private static async Task<Dictionary<Guid, Dictionary<string, (List<object> Prices, int Priority)>>> BuildFallbackPricingAsync(
        AppDbContext db, Guid? tenantId, IReadOnlyList<Guid> productIds,
        IReadOnlyList<IDictionary<string, object?>> items, IReadOnlyDictionary<Guid, CatalogPriceKind> knownKinds)
    {
        var map = new Dictionary<Guid, Dictionary<string, (List<object>, int)>>();
        if (productIds.Count == 0) return map;

        // Default variants (a variant-level fallback price resolves back to its owning product).
        var defaultVariants = await db.Set<CatalogProductVariant>().AsNoTracking()
            .Where(vr => productIds.Contains(vr.ProductId) && vr.IsDefault && vr.DeletedAt == null && (tenantId == null || vr.TenantId == tenantId))
            .Select(vr => new { vr.Id, vr.ProductId }).ToListAsync();
        var variantToProduct = defaultVariants.ToDictionary(vr => vr.Id, vr => vr.ProductId);
        var defaultVariantIds = variantToProduct.Keys.ToList();

        // Channels present on the listed offers (fallback prices scoped to those channels OR no channel).
        var channelIds = items
            .Select(i => Guid.TryParse(i.TryGetValue("channelId", out var v) ? v?.ToString() : null, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();

        var fallback = await db.Set<CatalogProductPrice>().AsNoTracking()
            .Where(pr => pr.OfferId == null && (tenantId == null || pr.TenantId == tenantId) &&
                ((pr.ProductId != null && productIds.Contains(pr.ProductId.Value)) ||
                 (pr.VariantId != null && defaultVariantIds.Contains(pr.VariantId.Value))) &&
                (pr.ChannelId == null || channelIds.Contains(pr.ChannelId.Value)))
            .ToListAsync();
        if (fallback.Count == 0) return map;

        var kindIds = fallback.Select(pr => pr.PriceKindId).Where(id => !knownKinds.ContainsKey(id)).Distinct().ToList();
        var extraKinds = kindIds.Count == 0 ? new List<CatalogPriceKind>()
            : await db.Set<CatalogPriceKind>().AsNoTracking().Where(k => kindIds.Contains(k.Id)).ToListAsync();
        CatalogPriceKind? Kind(Guid id) => knownKinds.TryGetValue(id, out var k) ? k : extraKinds.FirstOrDefault(x => x.Id == id);

        void Assign(Guid? productRef, Guid? channelRef, object payload, int priority)
        {
            if (productRef is not { } product) return;
            var bucket = map.TryGetValue(product, out var b) ? b : map[product] = new Dictionary<string, (List<object>, int)>(StringComparer.Ordinal);
            var channelKey = channelRef?.ToString() ?? DefaultChannelKey;
            if (bucket.TryGetValue(channelKey, out var existing))
            {
                if (existing.Item2 > priority) return;
                if (existing.Item2 == priority) { existing.Item1.Add(payload); return; }
            }
            bucket[channelKey] = (new List<object> { payload }, priority);
        }

        foreach (var pr in fallback)
        {
            var kind = Kind(pr.PriceKindId);
            var payload = new Dictionary<string, object?>
            {
                ["priceKindId"] = pr.PriceKindId.ToString(),
                ["priceKindCode"] = kind?.Code,
                ["priceKindTitle"] = kind?.Title,
                ["currencyCode"] = pr.CurrencyCode,
                ["unitPriceNet"] = pr.UnitPriceNet,
                ["unitPriceGross"] = pr.UnitPriceGross,
                ["displayMode"] = kind?.DisplayMode ?? "excluding-tax",
            };
            if (pr.VariantId is { } vid)
            {
                var productRef = variantToProduct.TryGetValue(vid, out var vp) ? vp : pr.ProductId;
                Assign(productRef, pr.ChannelId, payload, pr.ChannelId is not null ? 4 : 3);
            }
            else
            {
                Assign(pr.ProductId, pr.ChannelId, payload, pr.ChannelId is not null ? 2 : 1);
            }
        }
        return map;
    }
}
