using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Products CRUD — the port of upstream <c>api/products/route.ts</c>. List goes through the CRUD factory
/// with <c>UseIndexList</c> (snake_case DataQuery output + <c>cf_*</c> filter/sort via the query index,
/// matching OM's <c>mapApiItem</c>); create/update/delete dispatch to the <c>catalog.products.*</c>
/// command handlers (base row + categoryIds/tags). A post-list hook overlays the simple associations
/// (offers/channelIds, categories/categoryIds, tags) the OM list reads.
///
/// PARITY-TODO (deferred to later slices — documented in HANDOFF Task J): the pricing resolution
/// (<c>item.pricing</c> via CatalogPricingService), unit-conversion-normalized quantity, sales-channel
/// name lookup on offers, custom-field (<c>cf_*</c>) persistence, and the advanced association filters
/// (channelIds/categoryIds/tagIds intersection, configurable→is_configurable) beyond the index's
/// generic camelCase→snake_case doc-field matching.
/// </summary>
public sealed class ProductsRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };
    private static readonly string[] Manage = { "catalog.products.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogProduct> Config() => new()
    {
        BasePath = "catalog/products",
        EntityType = CatalogIndexEntity.Product,
        ResourceKind = "catalog.product",
        DefaultSortField = "createdAt",
        DefaultPageSize = 50,
        MaxPageSize = 100,
        UseIndexList = true,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = p => p.Id,
        DeletedAtSelector = p => p.DeletedAt,
        TenantIdSelector = p => p.TenantId,
        OrganizationIdSelector = p => p.OrganizationId,
        Sorts = Sorts(),
        ApplyFilters = ApplyFilters,
        // Pricing-context + (deferred) association params are consumed by the afterList decorator / are not
        // product doc fields — keep them out of the generic index filter matching.
        NonFilterParams = new[]
        {
            "channelId", "channelIds", "offerId", "userId", "userGroupId", "customerId", "customerGroupId",
            "quantity", "quantityUnit", "priceDate", "categoryIds", "tagIds",
        },
        ProjectItem = ProjectListItem,
        ListHook = OverlayAssociationsAsync,
        CreatedEvent = "catalog.product.created",
        UpdatedEvent = "catalog.product.updated",
        DeletedEvent = "catalog.product.deleted",
        ValidateCreate = Data.CatalogValidators.Product,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<ProductCreateInput, ProductResult>(
                "catalog.products.create",
                new ProductCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.ProductId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<ProductUpdateInput, ProductResult>(
                "catalog.products.update", new ProductUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.ProductId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Product id is required");
            var r = await m.Bus.ExecuteWithLog<ProductDeleteInput, ProductResult>(
                "catalog.products.delete", new ProductDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.ProductId, r.LogEntry);
        },
    };

    private static Dictionary<string, Func<IQueryable<CatalogProduct>, bool, IOrderedQueryable<CatalogProduct>>> Sorts() => new()
    {
        ["title"] = (q, d) => d ? q.OrderByDescending(p => p.Title) : q.OrderBy(p => p.Title),
        ["sku"] = (q, d) => d ? q.OrderByDescending(p => p.Sku) : q.OrderBy(p => p.Sku),
        ["createdAt"] = (q, d) => d ? q.OrderByDescending(p => p.CreatedAt) : q.OrderBy(p => p.CreatedAt),
        ["updatedAt"] = (q, d) => d ? q.OrderByDescending(p => p.UpdatedAt) : q.OrderBy(p => p.UpdatedAt),
    };

    /// <summary>Base-field filters for the fallback (non-index) list path. The index path resolves the
    /// same base fields via the doc; this covers the empty-index case + free-text search.</summary>
    private static IQueryable<CatalogProduct> ApplyFilters(IQueryable<CatalogProduct> q, CrudListQuery query, CommandContext ctx)
    {
        if (query.Filters.TryGetValue("productType", out var pt) && !string.IsNullOrWhiteSpace(pt))
            q = q.Where(p => p.ProductType == pt);
        if (query.Filters.TryGetValue("status", out var st) && Guid.TryParse(st, out var statusId))
            q = q.Where(p => p.StatusEntryId == statusId);
        if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
            q = q.Where(p => p.IsActive == active);
        if (query.Filters.TryGetValue("configurable", out var cf) && CatalogFilter.TryBool(cf, out var configurable))
            q = q.Where(p => p.IsConfigurable == configurable);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(p =>
                (p.Title != null && p.Title.ToLower().Contains(term)) ||
                (p.Subtitle != null && p.Subtitle.ToLower().Contains(term)) ||
                (p.Description != null && p.Description.ToLower().Contains(term)) ||
                (p.Sku != null && p.Sku.ToLower().Contains(term)) ||
                (p.Handle != null && p.Handle.ToLower().Contains(term)));
        }
        return q;
    }

    /// <summary>List-row projection in OM's snake_case DataQuery shape (the product list <c>fields</c> +
    /// <c>transformItem</c>). Matches the index base doc so index and fallback paths agree.</summary>
    internal static IDictionary<string, object?> ProjectListItem(CatalogProduct p)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = p.Id.ToString(),
            ["title"] = p.Title,
            ["subtitle"] = p.Subtitle,
            ["description"] = p.Description,
            ["sku"] = p.Sku,
            ["handle"] = p.Handle,
            ["tax_rate_id"] = p.TaxRateId?.ToString(),
            ["tax_rate"] = p.TaxRate,
            ["product_type"] = p.ProductType,
            ["status_entry_id"] = p.StatusEntryId?.ToString(),
            ["primary_currency_code"] = p.PrimaryCurrencyCode,
            ["default_unit"] = p.DefaultUnit,
            ["default_sales_unit"] = p.DefaultSalesUnit,
            ["default_sales_unit_quantity"] = p.DefaultSalesUnitQuantity,
            ["uom_rounding_scale"] = (int)p.UomRoundingScale,
            ["uom_rounding_mode"] = p.UomRoundingMode,
            ["unit_price_enabled"] = p.UnitPriceEnabled,
            ["unit_price_reference_unit"] = p.UnitPriceReferenceUnit,
            ["unit_price_base_quantity"] = p.UnitPriceBaseQuantity,
            ["default_media_id"] = p.DefaultMediaId?.ToString(),
            ["default_media_url"] = p.DefaultMediaUrl,
            ["weight_value"] = p.WeightValue,
            ["weight_unit"] = p.WeightUnit,
            ["dimensions"] = null,
            ["custom_fieldset_code"] = p.CustomFieldsetCode,
            ["option_schema_id"] = p.OptionSchemaId?.ToString(),
            ["is_configurable"] = p.IsConfigurable,
            ["is_active"] = p.IsActive,
            ["organization_id"] = p.OrganizationId.ToString(),
            ["tenant_id"] = p.TenantId.ToString(),
            ["created_at"] = CatalogHttp.Iso(p.CreatedAt),
            ["updated_at"] = CatalogHttp.Iso(p.UpdatedAt),
            // upstream transformItem composite (unit-code canonicalization is PARITY-TODO — pass-through).
            ["unit_price"] = new Dictionary<string, object?>
            {
                ["enabled"] = p.UnitPriceEnabled,
                ["reference_unit"] = p.UnitPriceReferenceUnit,
                ["base_quantity"] = p.UnitPriceBaseQuantity,
            },
        };
        return item;
    }

    /// <summary>Post-list decorator: overlay offers/channelIds, categories/categoryIds and tags the OM
    /// product list reads. Pricing/unit-conversion decoration is deferred (see class remarks).</summary>
    private static async Task OverlayAssociationsAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var ids = items
            .Select(i => i.TryGetValue("id", out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        if (ids.Count == 0) return;

        // Offers (+ channelIds). Sales-channel name lookup is deferred (sales module not ported).
        var offers = await db.Set<CatalogOffer>().AsNoTracking()
            .Where(o => ids.Contains(o.ProductId) && o.DeletedAt == null)
            .OrderBy(o => o.CreatedAt).ToListAsync();
        var offersByProduct = offers.GroupBy(o => o.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // Categories (+ parent names).
        var assignments = await db.Set<CatalogProductCategoryAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.ProductId)).OrderBy(a => a.Position).ToListAsync();
        var categoryIds = assignments.Select(a => a.CategoryId).Distinct().ToList();
        var categories = categoryIds.Count == 0 ? new List<CatalogProductCategory>()
            : await db.Set<CatalogProductCategory>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToListAsync();
        var categoryById = categories.ToDictionary(c => c.Id);
        var parentIds = categories.Where(c => c.ParentId is not null).Select(c => c.ParentId!.Value).Distinct().ToList();
        var parents = parentIds.Count == 0 ? new List<CatalogProductCategory>()
            : await db.Set<CatalogProductCategory>().AsNoTracking().Where(c => parentIds.Contains(c.Id)).ToListAsync();
        var parentNameById = parents.ToDictionary(c => c.Id, c => (string?)c.Name);
        var categoriesByProduct = assignments.GroupBy(a => a.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // Tags (labels).
        var tagAssignments = await db.Set<CatalogProductTagAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.ProductId)).ToListAsync();
        var tagIds = tagAssignments.Select(a => a.TagId).Distinct().ToList();
        var tags = tagIds.Count == 0 ? new List<CatalogProductTag>()
            : await db.Set<CatalogProductTag>().AsNoTracking().Where(t => tagIds.Contains(t.Id)).ToListAsync();
        var tagLabelById = tags.ToDictionary(t => t.Id, t => t.Label);
        var tagsByProduct = tagAssignments.GroupBy(a => a.ProductId).ToDictionary(g => g.Key, g => g.ToList());

        // Pricing resolution: candidate prices (product-level + this product's variants) resolved against
        // the request's PricingContext (upstream decorateProductsAfterList → item.pricing). Unit-conversion-
        // normalized quantity is deferred (uses the raw quantity).
        var pricingByProduct = await ResolveProductPricingAsync(db, http, tenantId: ctx.TenantId, productIds: ids);

        foreach (var item in items)
        {
            if (!Guid.TryParse(item.TryGetValue("id", out var v) ? v?.ToString() : null, out var pid)) continue;

            var offerList = offersByProduct.TryGetValue(pid, out var ofs) ? ofs : new List<CatalogOffer>();
            item["offers"] = offerList.Select(o => (object)new Dictionary<string, object?>
            {
                ["id"] = o.Id.ToString(),
                ["channelId"] = o.ChannelId.ToString(),
                ["channelName"] = null,
                ["channelCode"] = null,
                ["title"] = o.Title,
                ["description"] = o.Description,
                ["isActive"] = o.IsActive,
                ["defaultMediaId"] = o.DefaultMediaId?.ToString(),
                ["defaultMediaUrl"] = o.DefaultMediaUrl,
                ["updatedAt"] = CatalogHttp.Iso(o.UpdatedAt),
            }).ToList();
            item["channelIds"] = offerList.Select(o => o.ChannelId.ToString()).Distinct().ToList();

            var catAssignments = categoriesByProduct.TryGetValue(pid, out var cas) ? cas : new List<CatalogProductCategoryAssignment>();
            var cats = new List<object>();
            var catIdList = new List<string>();
            foreach (var a in catAssignments)
            {
                if (!categoryById.TryGetValue(a.CategoryId, out var c)) continue;
                var parentName = c.ParentId is { } pp && parentNameById.TryGetValue(pp, out var pn) ? pn : null;
                cats.Add(new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = c.Name,
                    ["treePath"] = c.TreePath,
                    ["parentId"] = c.ParentId?.ToString(),
                    ["parentName"] = parentName,
                });
                catIdList.Add(c.Id.ToString());
            }
            item["categories"] = cats;
            item["categoryIds"] = catIdList;

            var tagList = tagsByProduct.TryGetValue(pid, out var tas) ? tas : new List<CatalogProductTagAssignment>();
            item["tags"] = tagList
                .Select(a => tagLabelById.TryGetValue(a.TagId, out var label) ? label : null)
                .Where(l => !string.IsNullOrEmpty(l)).ToList();

            item["pricing"] = pricingByProduct.TryGetValue(pid, out var pricing) ? pricing : null;
        }
    }

    /// <summary>Resolve the best applicable price per product (base + variant prices) for the request's
    /// PricingContext, returning the upstream <c>item.pricing</c> shape keyed by product id.</summary>
    private static async Task<Dictionary<Guid, object>> ResolveProductPricingAsync(
        AppDbContext db, HttpContext http, Guid? tenantId, IReadOnlyList<Guid> productIds)
    {
        var result = new Dictionary<Guid, object>();
        if (productIds.Count == 0) return result;

        // Map each product's variants (variant price rows resolve back to the owning product).
        var variants = await db.Set<CatalogProductVariant>().AsNoTracking()
            .Where(vr => productIds.Contains(vr.ProductId) && vr.DeletedAt == null && (tenantId == null || vr.TenantId == tenantId))
            .Select(vr => new { vr.Id, vr.ProductId }).ToListAsync();
        var variantToProduct = variants.ToDictionary(vr => vr.Id, vr => vr.ProductId);
        var variantIds = variantToProduct.Keys.ToList();

        var priceRows = await db.Set<CatalogProductPrice>().AsNoTracking()
            .Where(pr => (tenantId == null || pr.TenantId == tenantId) &&
                ((pr.ProductId != null && productIds.Contains(pr.ProductId.Value)) ||
                 (pr.VariantId != null && variantIds.Contains(pr.VariantId.Value))))
            .ToListAsync();
        if (priceRows.Count == 0) return result;

        // Price-kind (code + promotion flag) + offer (channel) lookups for resolution.
        var kindIds = priceRows.Select(pr => pr.PriceKindId).Distinct().ToList();
        var kinds = (await db.Set<CatalogPriceKind>().AsNoTracking().Where(k => kindIds.Contains(k.Id)).ToListAsync())
            .ToDictionary(k => k.Id);
        var offerIds = priceRows.Where(pr => pr.OfferId is not null).Select(pr => pr.OfferId!.Value).Distinct().ToList();
        var offerChannel = offerIds.Count == 0 ? new Dictionary<Guid, Guid>()
            : (await db.Set<CatalogOffer>().AsNoTracking().Where(o => offerIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id, o => o.ChannelId);

        var candidatesByProduct = new Dictionary<Guid, List<CatalogPricing.Candidate>>();
        foreach (var pr in priceRows)
        {
            Guid? productId = pr.ProductId ?? (pr.VariantId is { } vid && variantToProduct.TryGetValue(vid, out var vp) ? vp : null);
            if (productId is not { } pid) continue;
            kinds.TryGetValue(pr.PriceKindId, out var kind);
            Guid? offerChan = pr.OfferId is { } oid && offerChannel.TryGetValue(oid, out var ch) ? ch : null;
            var candidate = new CatalogPricing.Candidate(
                pr.Id, pr.VariantId, pr.ProductId, pr.OfferId, pr.PriceKindId,
                kind?.Code, kind?.IsPromotion ?? false, offerChan,
                pr.Kind, pr.CurrencyCode, pr.MinQuantity, pr.MaxQuantity,
                pr.UnitPriceNet, pr.UnitPriceGross, pr.TaxRate, pr.TaxAmount,
                pr.ChannelId, pr.UserId, pr.UserGroupId, pr.CustomerId, pr.CustomerGroupId,
                pr.StartsAt, pr.EndsAt);
            (candidatesByProduct.TryGetValue(pid, out var list) ? list : candidatesByProduct[pid] = new()).Add(candidate);
        }

        var context = BuildPricingContext(http);
        // Per-product unit-conversion-normalized quantity (upstream normalizedQuantityForPricing): when
        // ?quantityUnit differs from a product's default_unit and the product has an active conversion, the
        // context quantity is scaled by that factor for this product only.
        var normalizedQuantity = await ResolveNormalizedQuantitiesAsync(db, http, tenantId, productIds, context.Quantity);
        foreach (var (pid, candidates) in candidatesByProduct)
        {
            var productContext = normalizedQuantity.TryGetValue(pid, out var nq) ? context with { Quantity = nq } : context;
            var best = CatalogPricing.SelectBest(candidates, productContext);
            if (best is null) continue;
            result[pid] = new Dictionary<string, object?>
            {
                ["kind"] = CatalogPricing.ResolveKindCode(best),
                ["price_kind_id"] = best.PriceKindId.ToString(),
                ["price_kind_code"] = CatalogPricing.ResolveKindCode(best),
                ["currency_code"] = best.CurrencyCode,
                ["unit_price_net"] = best.UnitPriceNet,
                ["unit_price_gross"] = best.UnitPriceGross,
                ["min_quantity"] = best.MinQuantity,
                ["max_quantity"] = best.MaxQuantity,
                ["tax_rate"] = best.TaxRate,
                ["tax_amount"] = best.TaxAmount,
                ["scope"] = new Dictionary<string, object?>
                {
                    ["variant_id"] = best.VariantId?.ToString(),
                    ["offer_id"] = best.OfferId?.ToString(),
                    ["channel_id"] = CatalogPricing.ResolveChannelId(best)?.ToString(),
                    ["user_id"] = best.UserId?.ToString(),
                    ["user_group_id"] = best.UserGroupId?.ToString(),
                    ["customer_id"] = best.CustomerId?.ToString(),
                    ["customer_group_id"] = best.CustomerGroupId?.ToString(),
                },
            };
        }
        return result;
    }

    /// <summary>Build the pricing context from the list query (upstream buildPricingContext): channel (or
    /// the single channelIds value), offer/user/customer scope, quantity (default 1), priceDate (default now).</summary>
    private static CatalogPricing.PricingContext BuildPricingContext(HttpContext http)
    {
        var q = http.Request.Query;
        Guid? G(string key) => Guid.TryParse(q[key].ToString(), out var g) ? g : null;

        var channelId = G("channelId");
        if (channelId is null && !string.IsNullOrWhiteSpace(q["channelIds"]))
        {
            var one = q["channelIds"].ToString().Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (one.Count == 1 && Guid.TryParse(one[0], out var g)) channelId = g;
        }
        var quantity = decimal.TryParse(q["quantity"].ToString(), System.Globalization.CultureInfo.InvariantCulture, out var qty) && qty > 0 ? qty : 1m;
        var date = DateTimeOffset.TryParse(q["priceDate"].ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTimeOffset.UtcNow;

        return new CatalogPricing.PricingContext(
            channelId, G("offerId"), G("userId"), G("userGroupId"), G("customerId"), G("customerGroupId"), quantity, date);
    }

    /// <summary>Per-product normalized quantity for pricing (upstream <c>normalizedQuantityForPricing</c>):
    /// when <c>?quantityUnit</c> is set and differs from a product's canonical <c>default_unit</c>, scale the
    /// context quantity by the product's active conversion factor for that unit. Products without a matching
    /// conversion (or whose base unit equals the requested unit) are omitted (keep the raw quantity).</summary>
    private static async Task<Dictionary<Guid, decimal>> ResolveNormalizedQuantitiesAsync(
        AppDbContext db, HttpContext http, Guid? tenantId, IReadOnlyList<Guid> productIds, decimal quantity)
    {
        var result = new Dictionary<Guid, decimal>();
        var quantityUnitKey = CatalogUnitCodes.Canonicalize(http.Request.Query["quantityUnit"].ToString());
        if (quantityUnitKey is null || productIds.Count == 0) return result;

        var products = await db.Set<CatalogProduct>().AsNoTracking()
            .Where(p => productIds.Contains(p.Id) && (tenantId == null || p.TenantId == tenantId))
            .Select(p => new { p.Id, p.DefaultUnit }).ToListAsync();

        var conversions = await db.Set<CatalogProductUnitConversion>().AsNoTracking()
            .Where(c => productIds.Contains(c.ProductId) && c.IsActive && c.DeletedAt == null && (tenantId == null || c.TenantId == tenantId))
            .Select(c => new { c.ProductId, c.UnitCode, c.ToBaseFactor }).ToListAsync();
        var factorByProduct = new Dictionary<Guid, decimal>();
        foreach (var c in conversions)
            if (c.ToBaseFactor > 0 && CatalogUnitCodes.Canonicalize(c.UnitCode) == quantityUnitKey)
                factorByProduct[c.ProductId] = c.ToBaseFactor;

        foreach (var p in products)
        {
            var baseUnit = CatalogUnitCodes.Canonicalize(p.DefaultUnit);
            if (baseUnit is null || baseUnit == quantityUnitKey) continue;
            if (!factorByProduct.TryGetValue(p.Id, out var factor)) continue;
            var normalized = quantity * factor;
            if (normalized > 0) result[p.Id] = normalized;
        }
        return result;
    }
}

/// <summary>Catalog entity ids ('&lt;module&gt;:&lt;entity&gt;') for the indexer + custom fields
/// (upstream ce.ts <c>E.catalog.*</c>). Kept in one place so routes and the base-row resolver agree.</summary>
public static class CatalogIndexEntity
{
    public const string Product = "catalog:catalog_product";
    public const string Variant = "catalog:catalog_product_variant";
    public const string Price = "catalog:catalog_product_price";
    public const string PriceKind = "catalog:catalog_price_kind";
    public const string Category = "catalog:catalog_product_category";
    public const string Offer = "catalog:catalog_offer";
    public const string UnitConversion = "catalog:catalog_product_unit_conversion";
    public const string OptionSchema = "catalog:catalog_option_schema_template";
}
