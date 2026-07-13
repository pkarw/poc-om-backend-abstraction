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
            var id = ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Product id is required");
            var r = await m.Bus.ExecuteWithLog<ProductDeleteInput, ProductResult>(
                "catalog.products.delete", new ProductDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.ProductId, r.LogEntry);
        },
    };

    internal static Guid? ResolveDeleteId(CrudMutationContext m)
    {
        if (CatalogHttp.GuidOf(m.Body, "id") is { } fromBody) return fromBody;
        if (m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g)) return g;
        return null;
    }

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
        if (query.Filters.TryGetValue("isActive", out var ia) && TryBool(ia, out var active))
            q = q.Where(p => p.IsActive == active);
        if (query.Filters.TryGetValue("configurable", out var cf) && TryBool(cf, out var configurable))
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

    private static bool TryBool(string raw, out bool value)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": value = true; return true;
            case "0": case "false": case "no": value = false; return true;
            default: value = false; return false;
        }
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

            // Pricing resolution deferred (CatalogPricingService not yet ported).
            item["pricing"] = null;
        }
    }
}

/// <summary>Catalog entity ids ('&lt;module&gt;:&lt;entity&gt;') for the indexer + custom fields
/// (upstream ce.ts <c>E.catalog.*</c>). Kept in one place so routes and the base-row resolver agree.</summary>
public static class CatalogIndexEntity
{
    public const string Product = "catalog:catalog_product";
}
