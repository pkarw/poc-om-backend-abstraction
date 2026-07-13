using System.Text.Json;

namespace OpenMercato.Modules.Catalog.Commands;

// Command input/result contracts for the catalog write surface. Create/update inputs carry the raw
// request body (JsonElement) so the handler can read base fields AND persist nested associations
// (categoryIds, tags) in the same transaction — mirroring the customers command contracts. Ids are
// strings for wire parity.

// ---- Products --------------------------------------------------------------------------------
public sealed record ProductCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record ProductUpdateInput(Guid Id, JsonElement Body);
public sealed record ProductDeleteInput(Guid Id);
public sealed record ProductResult(string? ProductId);

// ---- Categories ------------------------------------------------------------------------------
public sealed record CategoryCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record CategoryUpdateInput(Guid Id, JsonElement Body);
public sealed record CategoryDeleteInput(Guid Id);
public sealed record CategoryResult(string? CategoryId);

public sealed record CategorySnapshot(
    string Name, string? Slug, string? Description, string? ParentId, bool IsActive);

// ---- Product unit conversions ----------------------------------------------------------------
public sealed record UnitConversionCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record UnitConversionUpdateInput(Guid Id, JsonElement Body);
public sealed record UnitConversionDeleteInput(Guid Id);
public sealed record UnitConversionResult(string? ConversionId);

public sealed record UnitConversionSnapshot(string UnitCode, decimal ToBaseFactor, int SortOrder, bool IsActive);

// ---- Option schema templates -----------------------------------------------------------------
public sealed record OptionSchemaCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record OptionSchemaUpdateInput(Guid Id, JsonElement Body);
public sealed record OptionSchemaDeleteInput(Guid Id);
public sealed record OptionSchemaResult(string? SchemaId);

public sealed record OptionSchemaSnapshot(string Name, string? Code, string? Description, bool IsActive);

// ---- Offers ----------------------------------------------------------------------------------
public sealed record OfferCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record OfferUpdateInput(Guid Id, JsonElement Body);
public sealed record OfferDeleteInput(Guid Id);
public sealed record OfferResult(string? OfferId);

public sealed record OfferSnapshot(
    string Title, string? Description, string? DefaultMediaId, string? DefaultMediaUrl, bool IsActive);

// ---- Variants --------------------------------------------------------------------------------
public sealed record VariantCreateInput(JsonElement Body);
public sealed record VariantUpdateInput(Guid Id, JsonElement Body);
public sealed record VariantDeleteInput(Guid Id);
public sealed record VariantResult(string? VariantId);

public sealed record VariantSnapshot(
    string? Name, string? Sku, string? Barcode, string? StatusEntryId, bool IsDefault, bool IsActive,
    string? WeightUnit, string? CustomFieldsetCode);

// ---- Price kinds -----------------------------------------------------------------------------
public sealed record PriceKindCreateInput(Guid? OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PriceKindUpdateInput(Guid Id, JsonElement Body);
public sealed record PriceKindDeleteInput(Guid Id);
public sealed record PriceKindResult(string? PriceKindId);

public sealed record PriceKindSnapshot(
    string Code, string Title, string DisplayMode, string? CurrencyCode, bool IsPromotion, bool IsActive);

// ---- Prices ----------------------------------------------------------------------------------
public sealed record PriceCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PriceUpdateInput(Guid Id, JsonElement Body);
public sealed record PriceDeleteInput(Guid Id);
public sealed record PriceResult(string? PriceId);

/// <summary>Full price-row snapshot — used both for the changelog diff and to re-insert on undo of the
/// hard delete (catalog_product_variant_prices has no soft-delete column).</summary>
public sealed record PriceSnapshot(
    string? ProductId, string? VariantId, string? OfferId, string PriceKindId, string CurrencyCode,
    string Kind, int MinQuantity, int? MaxQuantity, decimal? UnitPriceNet, decimal? UnitPriceGross,
    decimal? TaxRate, decimal? TaxAmount, string? ChannelId, string? UserId, string? UserGroupId,
    string? CustomerId, string? CustomerGroupId, string? StartsAt, string? EndsAt, string? Metadata);

/// <summary>Changelog snapshot for a product's user-visible base fields (drives the auto-diff in
/// <c>CommandBus.PersistLog</c>). Bookkeeping fields (id/createdAt/updatedAt) are excluded by the diff.</summary>
public sealed record ProductSnapshot(
    string Title,
    string? Subtitle,
    string? Description,
    string? Sku,
    string? Handle,
    string ProductType,
    string? StatusEntryId,
    string? PrimaryCurrencyCode,
    string? DefaultUnit,
    string? DefaultSalesUnit,
    decimal DefaultSalesUnitQuantity,
    bool UnitPriceEnabled,
    string? UnitPriceReferenceUnit,
    string? CustomFieldsetCode,
    string? OptionSchemaId,
    bool IsConfigurable,
    bool IsActive);
