using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.Catalog.Lib;

/// <summary>
/// Catalog-owned <see cref="IIndexBaseRowResolver"/> — resolves the BASE row for catalog read models that
/// own their own physical tables (products, variants, prices, price-kinds), so the query index carries
/// their base columns for filter/sort/search. Registered as a decorator in front of whatever resolver is
/// already registered (customers' or the storage default): every non-catalog entity type delegates to
/// <see cref="_fallback"/>, so the resolver chain stays intact regardless of module registration order.
/// See <see cref="CatalogModule.ConfigureServices"/>.
/// </summary>
public sealed class CatalogIndexBaseRowResolver : IIndexBaseRowResolver
{
    private readonly AppDbContext _db;
    private readonly IIndexBaseRowResolver _fallback;

    public const string ProductEntityType = CatalogIndexEntity.Product;

    public CatalogIndexBaseRowResolver(AppDbContext db, IIndexBaseRowResolver fallback)
    {
        _db = db;
        _fallback = fallback;
    }

    public async Task<IReadOnlyDictionary<string, object?>?> LoadAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(recordId, out var id) && IsCatalog(entityType)) return null;

        switch (entityType)
        {
            case CatalogIndexEntity.Product:
            {
                var p = await _db.Set<CatalogProduct>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
                return p is null ? null : ProjectProductDoc(p);
            }
            case CatalogIndexEntity.Variant:
            {
                var v = await _db.Set<CatalogProductVariant>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
                return v is null ? null : ProjectVariantDoc(v);
            }
            case CatalogIndexEntity.Price:
            {
                var pr = await _db.Set<CatalogProductPrice>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                return pr is null ? null : ProjectPriceDoc(pr);
            }
            case CatalogIndexEntity.PriceKind:
            {
                var k = await _db.Set<CatalogPriceKind>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
                return k is null ? null : ProjectPriceKindDoc(k);
            }
            default:
                return await _fallback.LoadAsync(entityType, recordId, organizationId, tenantId, ct);
        }
    }

    public async Task<IReadOnlyList<(string RecordId, Guid? OrganizationId, Guid? TenantId)>?> EnumerateRecordIdsAsync(
        string entityType, Guid? tenantId, CancellationToken ct = default)
    {
        switch (entityType)
        {
            case CatalogIndexEntity.Product:
            {
                var rows = await _db.Set<CatalogProduct>().AsNoTracking()
                    .Where(p => p.DeletedAt == null).Where(p => tenantId == null || p.TenantId == tenantId)
                    .Select(p => new { p.Id, Org = (Guid?)p.OrganizationId, p.TenantId }).ToListAsync(ct);
                return rows.Select(r => (r.Id.ToString(), r.Org, (Guid?)r.TenantId)).ToList();
            }
            case CatalogIndexEntity.Variant:
            {
                var rows = await _db.Set<CatalogProductVariant>().AsNoTracking()
                    .Where(v => v.DeletedAt == null).Where(v => tenantId == null || v.TenantId == tenantId)
                    .Select(v => new { v.Id, Org = (Guid?)v.OrganizationId, v.TenantId }).ToListAsync(ct);
                return rows.Select(r => (r.Id.ToString(), r.Org, (Guid?)r.TenantId)).ToList();
            }
            case CatalogIndexEntity.Price:
            {
                var rows = await _db.Set<CatalogProductPrice>().AsNoTracking()
                    .Where(pr => tenantId == null || pr.TenantId == tenantId)
                    .Select(pr => new { pr.Id, Org = (Guid?)pr.OrganizationId, pr.TenantId }).ToListAsync(ct);
                return rows.Select(r => (r.Id.ToString(), r.Org, (Guid?)r.TenantId)).ToList();
            }
            case CatalogIndexEntity.PriceKind:
            {
                var rows = await _db.Set<CatalogPriceKind>().AsNoTracking()
                    .Where(k => k.DeletedAt == null).Where(k => tenantId == null || k.TenantId == tenantId)
                    .Select(k => new { k.Id, k.OrganizationId, k.TenantId }).ToListAsync(ct);
                return rows.Select(r => (r.Id.ToString(), r.OrganizationId, (Guid?)r.TenantId)).ToList();
            }
            default:
                return await _fallback.EnumerateRecordIdsAsync(entityType, tenantId, ct);
        }
    }

    private static bool IsCatalog(string entityType) => entityType
        is CatalogIndexEntity.Product or CatalogIndexEntity.Variant
        or CatalogIndexEntity.Price or CatalogIndexEntity.PriceKind;

    private static string? Iso(DateTimeOffset? v) => v?.ToUniversalTime().ToString("o");

    /// <summary>Project a <c>catalog_products</c> row into the index base doc (snake_case keys matching the
    /// product list <c>fields</c>).</summary>
    internal static IReadOnlyDictionary<string, object?> ProjectProductDoc(CatalogProduct p) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
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
            ["custom_fieldset_code"] = p.CustomFieldsetCode,
            ["option_schema_id"] = p.OptionSchemaId?.ToString(),
            ["is_configurable"] = p.IsConfigurable,
            ["is_active"] = p.IsActive,
            ["organization_id"] = p.OrganizationId.ToString(),
            ["tenant_id"] = p.TenantId.ToString(),
            ["created_at"] = Iso(p.CreatedAt),
            ["updated_at"] = Iso(p.UpdatedAt),
        };

    /// <summary>Project a <c>catalog_product_variants</c> row into the index base doc (variant list fields).</summary>
    internal static IReadOnlyDictionary<string, object?> ProjectVariantDoc(CatalogProductVariant v) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = v.Id.ToString(),
            ["product_id"] = v.ProductId.ToString(),
            ["name"] = v.Name,
            ["sku"] = v.Sku,
            ["barcode"] = v.Barcode,
            ["status_entry_id"] = v.StatusEntryId,
            ["is_default"] = v.IsDefault,
            ["is_active"] = v.IsActive,
            ["weight_value"] = v.WeightValue,
            ["weight_unit"] = v.WeightUnit,
            ["tax_rate_id"] = v.TaxRateId?.ToString(),
            ["tax_rate"] = v.TaxRate,
            ["custom_fieldset_code"] = v.CustomFieldsetCode,
            ["default_media_id"] = v.DefaultMediaId?.ToString(),
            ["default_media_url"] = v.DefaultMediaUrl,
            ["organization_id"] = v.OrganizationId.ToString(),
            ["tenant_id"] = v.TenantId.ToString(),
            ["created_at"] = Iso(v.CreatedAt),
            ["updated_at"] = Iso(v.UpdatedAt),
        };

    /// <summary>Project a <c>catalog_product_variant_prices</c> row into the index base doc (price list fields).</summary>
    internal static IReadOnlyDictionary<string, object?> ProjectPriceDoc(CatalogProductPrice pr) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = pr.Id.ToString(),
            ["product_id"] = pr.ProductId?.ToString(),
            ["variant_id"] = pr.VariantId?.ToString(),
            ["offer_id"] = pr.OfferId?.ToString(),
            ["price_kind_id"] = pr.PriceKindId.ToString(),
            ["currency_code"] = pr.CurrencyCode,
            ["kind"] = pr.Kind,
            ["min_quantity"] = pr.MinQuantity,
            ["max_quantity"] = pr.MaxQuantity,
            ["unit_price_net"] = pr.UnitPriceNet,
            ["unit_price_gross"] = pr.UnitPriceGross,
            ["tax_rate"] = pr.TaxRate,
            ["tax_amount"] = pr.TaxAmount,
            ["channel_id"] = pr.ChannelId?.ToString(),
            ["user_id"] = pr.UserId?.ToString(),
            ["user_group_id"] = pr.UserGroupId?.ToString(),
            ["customer_id"] = pr.CustomerId?.ToString(),
            ["customer_group_id"] = pr.CustomerGroupId?.ToString(),
            ["starts_at"] = Iso(pr.StartsAt),
            ["ends_at"] = Iso(pr.EndsAt),
            ["organization_id"] = pr.OrganizationId.ToString(),
            ["tenant_id"] = pr.TenantId.ToString(),
            ["created_at"] = Iso(pr.CreatedAt),
            ["updated_at"] = Iso(pr.UpdatedAt),
        };

    /// <summary>Project a <c>catalog_price_kinds</c> row into the index base doc (price-kind list fields).</summary>
    internal static IReadOnlyDictionary<string, object?> ProjectPriceKindDoc(CatalogPriceKind k) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = k.Id.ToString(),
            ["organization_id"] = k.OrganizationId?.ToString(),
            ["tenant_id"] = k.TenantId.ToString(),
            ["code"] = k.Code,
            ["title"] = k.Title,
            ["display_mode"] = k.DisplayMode,
            ["currency_code"] = k.CurrencyCode,
            ["is_promotion"] = k.IsPromotion,
            ["is_active"] = k.IsActive,
            ["created_at"] = Iso(k.CreatedAt),
            ["updated_at"] = Iso(k.UpdatedAt),
        };
}
