using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.Catalog.Lib;

/// <summary>
/// Catalog-owned <see cref="IIndexBaseRowResolver"/> — resolves the BASE row for catalog read models that
/// own their own physical tables (currently <c>catalog:catalog_product</c> → <c>catalog_products</c>), so
/// the query index carries the product base columns for filter/sort/search. Registered as a decorator in
/// front of whatever resolver is already registered (customers' or the storage default): every
/// non-catalog entity type delegates to <see cref="_fallback"/>, so the resolver chain stays intact
/// regardless of module registration order. See <see cref="CatalogModule.ConfigureServices"/>.
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
        if (entityType != ProductEntityType)
            return await _fallback.LoadAsync(entityType, recordId, organizationId, tenantId, ct);

        if (!Guid.TryParse(recordId, out var id)) return null;
        var p = await _db.Set<CatalogProduct>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        return p is null ? null : ProjectProductDoc(p);
    }

    public async Task<IReadOnlyList<(string RecordId, Guid? OrganizationId, Guid? TenantId)>?> EnumerateRecordIdsAsync(
        string entityType, Guid? tenantId, CancellationToken ct = default)
    {
        if (entityType != ProductEntityType)
            return await _fallback.EnumerateRecordIdsAsync(entityType, tenantId, ct);

        var rows = await _db.Set<CatalogProduct>().AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(p => tenantId == null || p.TenantId == tenantId)
            .Select(p => new { p.Id, p.OrganizationId, p.TenantId })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id.ToString(), (Guid?)r.OrganizationId, (Guid?)r.TenantId)).ToList();
    }

    /// <summary>Project a <c>catalog_products</c> row into the index base doc (snake_case keys matching the
    /// product list <c>fields</c>), so <c>catalog:catalog_product</c> lists filter/sort by base + cf fields.</summary>
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
            ["created_at"] = p.CreatedAt.ToUniversalTime().ToString("o"),
            ["updated_at"] = p.UpdatedAt.ToUniversalTime().ToString("o"),
        };
}
