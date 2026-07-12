namespace OpenMercato.Modules.Catalog.Data;

// Port of upstream packages/core/src/modules/catalog/data/entities.ts (12 @Entity classes).
// PascalCase props map to the exact snake_case columns of the OM-owned catalog_* schema (OM's
// MikroORM migrations create the physical tables; the testbench runs the .NET port migrations-off).
// EF only maps the runtime model (CatalogModule.ConfigureModel). Conventions mirror the customers
// port: jsonb columns are raw JSON strings, numeric columns are decimal, MikroORM relations become
// scalar FK Guids, and every table carries organization_id + tenant_id (catalog_price_kinds allows a
// null organization_id — tenant-global price kinds). Soft-delete (deleted_at) only where noted.

// -----------------------------------------------------------------------------------------------
// 1. CatalogOptionSchemaTemplate — catalog_product_option_schemas (reusable option-schema template)
// -----------------------------------------------------------------------------------------------
/// <summary>Named, reusable product option schema (upstream <c>CatalogOptionSchemaTemplate</c>).
/// UNIQUE (organization_id, tenant_id, code). Soft-delete.</summary>
public sealed class CatalogOptionSchemaTemplate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>jsonb — the <c>CatalogProductOptionSchema</c> shape (options[]). Raw JSON.</summary>
    public string? Schema { get; set; }
    public string? Metadata { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 2. CatalogProduct — catalog_products (the sellable product / service base record)
// -----------------------------------------------------------------------------------------------
/// <summary>The base catalog product (upstream <c>CatalogProduct</c>). UNIQUE (scope, sku) and
/// (scope, handle). <c>option_schema_id</c> FK → option-schema template (ON DELETE SET NULL).
/// Soft-delete.</summary>
public sealed class CatalogProduct
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Description { get; set; }
    public string? Sku { get; set; }
    public string? Handle { get; set; }
    public Guid? TaxRateId { get; set; }
    public decimal? TaxRate { get; set; }
    public string ProductType { get; set; } = "simple";
    public Guid? StatusEntryId { get; set; }
    public string? PrimaryCurrencyCode { get; set; }
    public string? DefaultUnit { get; set; }
    public string? DefaultSalesUnit { get; set; }
    public decimal DefaultSalesUnitQuantity { get; set; } = 1m;
    public short UomRoundingScale { get; set; } = 4;
    public string UomRoundingMode { get; set; } = "half_up";
    public bool UnitPriceEnabled { get; set; }
    public string? UnitPriceReferenceUnit { get; set; }
    public decimal? UnitPriceBaseQuantity { get; set; }
    public Guid? DefaultMediaId { get; set; }
    public string? DefaultMediaUrl { get; set; }
    public decimal? WeightValue { get; set; }
    public string? WeightUnit { get; set; }
    public string? Dimensions { get; set; }
    public string? Metadata { get; set; }
    public string? CustomFieldsetCode { get; set; }
    /// <summary>FK → catalog_product_option_schemas.id (nullable, ON DELETE SET NULL).</summary>
    public Guid? OptionSchemaId { get; set; }
    public bool IsConfigurable { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 3. CatalogProductUnitConversion — catalog_product_unit_conversions
// -----------------------------------------------------------------------------------------------
/// <summary>Per-product alternate unit → base-unit factor (upstream <c>CatalogProductUnitConversion</c>).
/// UNIQUE (product, unit_code). product_id FK ON DELETE CASCADE. Soft-delete.</summary>
public sealed class CatalogProductUnitConversion
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string UnitCode { get; set; } = string.Empty;
    public decimal ToBaseFactor { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 4. CatalogProductCategory — catalog_product_categories (materialized tree)
// -----------------------------------------------------------------------------------------------
/// <summary>Product category node with a materialized-path tree (upstream
/// <c>CatalogProductCategory</c>). UNIQUE (scope, slug). ancestor/child/descendant ids are jsonb
/// arrays (NOT NULL, default []). Soft-delete.</summary>
public sealed class CatalogProductCategory
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? RootId { get; set; }
    public string? TreePath { get; set; }
    public int Depth { get; set; }
    public string AncestorIds { get; set; } = "[]";
    public string ChildIds { get; set; } = "[]";
    public string DescendantIds { get; set; } = "[]";
    public string? Metadata { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 5. CatalogProductCategoryAssignment — catalog_product_category_assignments
// -----------------------------------------------------------------------------------------------
/// <summary>Product ↔ category membership (upstream <c>CatalogProductCategoryAssignment</c>).
/// UNIQUE (product, category). Both FKs ON DELETE CASCADE. No soft-delete.</summary>
public sealed class CatalogProductCategoryAssignment
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid CategoryId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 6. CatalogProductTag — catalog_product_tags
// -----------------------------------------------------------------------------------------------
/// <summary>Product tag pool (upstream <c>CatalogProductTag</c>). UNIQUE (scope, slug).
/// No soft-delete.</summary>
public sealed class CatalogProductTag
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 7. CatalogProductTagAssignment — catalog_product_tag_assignments
// -----------------------------------------------------------------------------------------------
/// <summary>Product ↔ tag membership (upstream <c>CatalogProductTagAssignment</c>).
/// UNIQUE (product, tag). Both FKs ON DELETE CASCADE. No soft-delete.</summary>
public sealed class CatalogProductTagAssignment
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid TagId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 8. CatalogOffer — catalog_product_offers (per-channel offer of a product)
// -----------------------------------------------------------------------------------------------
/// <summary>Per-channel offer of a product (upstream <c>CatalogOffer</c>).
/// UNIQUE (product, scope, channel_id). product_id FK ON DELETE CASCADE. Soft-delete.</summary>
public sealed class CatalogOffer
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ChannelId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DefaultMediaId { get; set; }
    public string? DefaultMediaUrl { get; set; }
    public string? Metadata { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 9. CatalogProductVariant — catalog_product_variants
// -----------------------------------------------------------------------------------------------
/// <summary>A concrete SKU-level variant of a product (upstream <c>CatalogProductVariant</c>).
/// UNIQUE (scope, sku). product_id FK (no delete rule). <c>status_entry_id</c> is text here (not a
/// uuid, unlike the product column). option_values is a jsonb code→value map. Soft-delete.</summary>
public sealed class CatalogProductVariant
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string? Name { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? StatusEntryId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public decimal? WeightValue { get; set; }
    public string? WeightUnit { get; set; }
    public string? Dimensions { get; set; }
    public string? Metadata { get; set; }
    public Guid? TaxRateId { get; set; }
    public decimal? TaxRate { get; set; }
    public string? OptionValues { get; set; }
    public Guid? DefaultMediaId { get; set; }
    public string? DefaultMediaUrl { get; set; }
    public string? CustomFieldsetCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 10. CatalogProductVariantRelation — catalog_product_variant_relations (bundle/grouped composition)
// -----------------------------------------------------------------------------------------------
/// <summary>Composition edge between a parent variant and a child variant/product (upstream
/// <c>CatalogProductVariantRelation</c>) for bundle/grouped products. All FKs ON DELETE CASCADE;
/// child_variant_id and child_product_id are nullable. No soft-delete.</summary>
public sealed class CatalogProductVariantRelation
{
    public Guid Id { get; set; }
    public Guid ParentVariantId { get; set; }
    public Guid? ChildVariantId { get; set; }
    public Guid? ChildProductId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string RelationType { get; set; } = "grouped";
    public bool IsRequired { get; set; }
    public int? MinQuantity { get; set; }
    public int? MaxQuantity { get; set; }
    public int Position { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 11. CatalogPriceKind — catalog_price_kinds (tenant-global price list / kind)
// -----------------------------------------------------------------------------------------------
/// <summary>A named price kind / list (upstream <c>CatalogPriceKind</c>). organization_id is
/// nullable (tenant-global). UNIQUE (tenant_id, code). Soft-delete.</summary>
public sealed class CatalogPriceKind
{
    public Guid Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DisplayMode { get; set; } = "excluding-tax";
    public string? CurrencyCode { get; set; }
    public bool IsPromotion { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 12. CatalogProductPrice — catalog_product_variant_prices (tiered/scoped price row)
// -----------------------------------------------------------------------------------------------
/// <summary>A single scoped/tiered price row (upstream <c>CatalogProductPrice</c>). Optionally
/// linked to a variant, product and offer; always linked to a price_kind (ON DELETE RESTRICT).
/// Scope columns (channel/user/user_group/customer/customer_group) narrow applicability. No
/// soft-delete.</summary>
public sealed class CatalogProductPrice
{
    public Guid Id { get; set; }
    public Guid? VariantId { get; set; }
    public Guid? ProductId { get; set; }
    public Guid? OfferId { get; set; }
    public Guid PriceKindId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public string Kind { get; set; } = "regular";
    public int MinQuantity { get; set; } = 1;
    public int? MaxQuantity { get; set; }
    public decimal? UnitPriceNet { get; set; }
    public decimal? UnitPriceGross { get; set; }
    public decimal? TaxRate { get; set; }
    public decimal? TaxAmount { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? UserGroupId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? CustomerGroupId { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
