using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog;

/// <summary>
/// The catalog module (upstream packages/core/src/modules/catalog) — products, variants, categories,
/// tags, offers, prices, price-kinds, option-schema templates and unit-conversions. OM owns + migrates
/// the <c>catalog_*</c> schema (the testbench runs the .NET port migrations-off); <see cref="ConfigureModel"/>
/// wires only the runtime EF model onto the shared AppDbContext. This foundation lays down the full
/// declaration surface (7 ACL features, 18 events, 1 notification type, default role features) plus the
/// 12-entity model; phase agents add routes and command handlers incrementally — see MapRoutes.
/// </summary>
public sealed class CatalogModule : IModule
{
    public string Id => "catalog";

    // -------------------------------------------------------------------------------------------
    // ACL — 7 features (acl.ts). Bare ids kept for back-compat; titles below are byte-exact. The
    // upstream dependsOn graph (products.view → currencies.view + dictionaries.view; manage → view;
    // pricing.manage → products.view + currencies.view; etc.) is documented but not modeled here —
    // the .NET AclFeatureDefinition carries (id, title) only, matching the customers port.
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "catalog.products.view",
        "catalog.products.manage",
        "catalog.categories.view",
        "catalog.categories.manage",
        "catalog.variants.manage",
        "catalog.pricing.manage",
        "catalog.settings.manage",
    };

    /// <summary>The 7 ACL features with byte-exact titles (acl.ts, module 'catalog').</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("catalog.products.view", "View catalog products"),
        new AclFeatureDefinition("catalog.products.manage", "Manage catalog products"),
        new AclFeatureDefinition("catalog.categories.view", "View catalog categories"),
        new AclFeatureDefinition("catalog.categories.manage", "Manage catalog categories"),
        new AclFeatureDefinition("catalog.variants.manage", "Manage catalog variants"),
        new AclFeatureDefinition("catalog.pricing.manage", "Manage catalog pricing"),
        new AclFeatureDefinition("catalog.settings.manage", "Manage catalog settings"),
    };

    /// <summary>Default role features (setup.ts): admin gets the <c>catalog.*</c> wildcard (upstream
    /// also lists variants.manage + pricing.manage redundantly under the wildcard); employee gets 6
    /// explicit grants (NOT settings.manage).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["admin"] = new[] { "catalog.*", "catalog.variants.manage", "catalog.pricing.manage" },
            ["employee"] = new[]
            {
                "catalog.products.view",
                "catalog.products.manage",
                "catalog.categories.view",
                "catalog.categories.manage",
                "catalog.variants.manage",
                "catalog.pricing.manage",
            },
        };

    // -------------------------------------------------------------------------------------------
    // Notifications — 1 type (notifications.ts): low-stock warning, retained 72h (3 days).
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<NotificationTypeDefinition> NotificationTypes { get; } = new[]
    {
        new NotificationTypeDefinition("catalog.product.low_stock", "warning", 72),
    };

    // -------------------------------------------------------------------------------------------
    // Events — 18 (events.ts). The 16 CRUD events are declared persistent (emitCrudSideEffects);
    // products additionally client-broadcast. The 2 pricing.resolve lifecycle events are transient
    // hooks (excludeFromTriggers) — declared non-persistent.
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
    {
        // Products (clientBroadcast)
        new EventDeclaration("catalog.product.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.product.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.product.deleted", "{ id, organizationId, tenantId }", true),
        // Product unit conversions
        new EventDeclaration("catalog.product_unit_conversion.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.product_unit_conversion.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.product_unit_conversion.deleted", "{ id, organizationId, tenantId }", true),
        // Categories
        new EventDeclaration("catalog.category.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.category.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.category.deleted", "{ id, organizationId, tenantId }", true),
        // Variants
        new EventDeclaration("catalog.variant.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.variant.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.variant.deleted", "{ id, organizationId, tenantId }", true),
        // Prices
        new EventDeclaration("catalog.price.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.price.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("catalog.price.deleted", "{ id, organizationId, tenantId }", true),
        // Pricing resolution lifecycle (excludeFromTriggers — transient hooks)
        new EventDeclaration("catalog.pricing.resolve.before", "{ scope, ... }", false),
        new EventDeclaration("catalog.pricing.resolve.after", "{ scope, resolved, ... }", false),
    };

    // -------------------------------------------------------------------------------------------
    // Services — phase agents register command handlers / workers / subscribers here (mirrors the
    // customers module's reflection-discovery of ICommand + ICatalogRouteGroup once routes land).
    // -------------------------------------------------------------------------------------------
    public void ConfigureServices(IServiceCollection services)
    {
    }

    // -------------------------------------------------------------------------------------------
    // Routes — added incrementally per route group in later phases.
    // -------------------------------------------------------------------------------------------
    public void MapRoutes(IEndpointRouteBuilder routes)
    {
    }

    // -------------------------------------------------------------------------------------------
    // Model — all 12 entities mapped byte-exact onto the shared AppDbContext (OM-owned schema).
    // -------------------------------------------------------------------------------------------
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogOptionSchemaTemplate>(e =>
        {
            e.ToTable("catalog_product_option_schemas");
            e.HasKey(x => x.Id).HasName("catalog_product_option_schemas_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Code).HasColumnName("code").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Schema).HasColumnName("schema").HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProduct>(e =>
        {
            e.ToTable("catalog_products");
            e.HasKey(x => x.Id).HasName("catalog_products_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.Subtitle).HasColumnName("subtitle");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Sku).HasColumnName("sku");
            e.Property(x => x.Handle).HasColumnName("handle");
            e.Property(x => x.TaxRateId).HasColumnName("tax_rate_id");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            e.Property(x => x.ProductType).HasColumnName("product_type").IsRequired();
            e.Property(x => x.StatusEntryId).HasColumnName("status_entry_id");
            e.Property(x => x.PrimaryCurrencyCode).HasColumnName("primary_currency_code");
            e.Property(x => x.DefaultUnit).HasColumnName("default_unit");
            e.Property(x => x.DefaultSalesUnit).HasColumnName("default_sales_unit");
            e.Property(x => x.DefaultSalesUnitQuantity).HasColumnName("default_sales_unit_quantity").HasColumnType("numeric(18,6)");
            e.Property(x => x.UomRoundingScale).HasColumnName("uom_rounding_scale");
            e.Property(x => x.UomRoundingMode).HasColumnName("uom_rounding_mode").IsRequired();
            e.Property(x => x.UnitPriceEnabled).HasColumnName("unit_price_enabled");
            e.Property(x => x.UnitPriceReferenceUnit).HasColumnName("unit_price_reference_unit");
            e.Property(x => x.UnitPriceBaseQuantity).HasColumnName("unit_price_base_quantity").HasColumnType("numeric(18,6)");
            e.Property(x => x.DefaultMediaId).HasColumnName("default_media_id");
            e.Property(x => x.DefaultMediaUrl).HasColumnName("default_media_url");
            e.Property(x => x.WeightValue).HasColumnName("weight_value").HasColumnType("numeric(16,4)");
            e.Property(x => x.WeightUnit).HasColumnName("weight_unit");
            e.Property(x => x.Dimensions).HasColumnName("dimensions").HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CustomFieldsetCode).HasColumnName("custom_fieldset_code");
            e.Property(x => x.OptionSchemaId).HasColumnName("option_schema_id");
            e.Property(x => x.IsConfigurable).HasColumnName("is_configurable");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductUnitConversion>(e =>
        {
            e.ToTable("catalog_product_unit_conversions");
            e.HasKey(x => x.Id).HasName("catalog_product_unit_conversions_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UnitCode).HasColumnName("unit_code").IsRequired();
            e.Property(x => x.ToBaseFactor).HasColumnName("to_base_factor").HasColumnType("numeric(24,12)");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductCategory>(e =>
        {
            e.ToTable("catalog_product_categories");
            e.HasKey(x => x.Id).HasName("catalog_product_categories_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Slug).HasColumnName("slug");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.Property(x => x.RootId).HasColumnName("root_id");
            e.Property(x => x.TreePath).HasColumnName("tree_path");
            e.Property(x => x.Depth).HasColumnName("depth");
            e.Property(x => x.AncestorIds).HasColumnName("ancestor_ids").HasColumnType("jsonb");
            e.Property(x => x.ChildIds).HasColumnName("child_ids").HasColumnType("jsonb");
            e.Property(x => x.DescendantIds).HasColumnName("descendant_ids").HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductCategoryAssignment>(e =>
        {
            e.ToTable("catalog_product_category_assignments");
            e.HasKey(x => x.Id).HasName("catalog_product_category_assignments_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductTag>(e =>
        {
            e.ToTable("catalog_product_tags");
            e.HasKey(x => x.Id).HasName("catalog_product_tags_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.Slug).HasColumnName("slug").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductTagAssignment>(e =>
        {
            e.ToTable("catalog_product_tag_assignments");
            e.HasKey(x => x.Id).HasName("catalog_product_tag_assignments_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.TagId).HasColumnName("tag_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogOffer>(e =>
        {
            e.ToTable("catalog_product_offers");
            e.HasKey(x => x.Id).HasName("catalog_product_offers_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.DefaultMediaId).HasColumnName("default_media_id");
            e.Property(x => x.DefaultMediaUrl).HasColumnName("default_media_url");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductVariant>(e =>
        {
            e.ToTable("catalog_product_variants");
            e.HasKey(x => x.Id).HasName("catalog_product_variants_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Sku).HasColumnName("sku");
            e.Property(x => x.Barcode).HasColumnName("barcode");
            e.Property(x => x.StatusEntryId).HasColumnName("status_entry_id");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.WeightValue).HasColumnName("weight_value").HasColumnType("numeric(16,4)");
            e.Property(x => x.WeightUnit).HasColumnName("weight_unit");
            e.Property(x => x.Dimensions).HasColumnName("dimensions").HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.TaxRateId).HasColumnName("tax_rate_id");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            e.Property(x => x.OptionValues).HasColumnName("option_values").HasColumnType("jsonb");
            e.Property(x => x.DefaultMediaId).HasColumnName("default_media_id");
            e.Property(x => x.DefaultMediaUrl).HasColumnName("default_media_url");
            e.Property(x => x.CustomFieldsetCode).HasColumnName("custom_fieldset_code");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductVariantRelation>(e =>
        {
            e.ToTable("catalog_product_variant_relations");
            e.HasKey(x => x.Id).HasName("catalog_product_variant_relations_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ParentVariantId).HasColumnName("parent_variant_id");
            e.Property(x => x.ChildVariantId).HasColumnName("child_variant_id");
            e.Property(x => x.ChildProductId).HasColumnName("child_product_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.RelationType).HasColumnName("relation_type").IsRequired();
            e.Property(x => x.IsRequired).HasColumnName("is_required");
            e.Property(x => x.MinQuantity).HasColumnName("min_quantity");
            e.Property(x => x.MaxQuantity).HasColumnName("max_quantity");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogPriceKind>(e =>
        {
            e.ToTable("catalog_price_kinds");
            e.HasKey(x => x.Id).HasName("catalog_price_kinds_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired();
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.DisplayMode).HasColumnName("display_mode").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code");
            e.Property(x => x.IsPromotion).HasColumnName("is_promotion");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CatalogProductPrice>(e =>
        {
            e.ToTable("catalog_product_variant_prices");
            e.HasKey(x => x.Id).HasName("catalog_product_variant_prices_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.VariantId).HasColumnName("variant_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.OfferId).HasColumnName("offer_id");
            e.Property(x => x.PriceKindId).HasColumnName("price_kind_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").IsRequired();
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.MinQuantity).HasColumnName("min_quantity");
            e.Property(x => x.MaxQuantity).HasColumnName("max_quantity");
            e.Property(x => x.UnitPriceNet).HasColumnName("unit_price_net").HasColumnType("numeric(16,4)");
            e.Property(x => x.UnitPriceGross).HasColumnName("unit_price_gross").HasColumnType("numeric(16,4)");
            e.Property(x => x.TaxRate).HasColumnName("tax_rate").HasColumnType("numeric(7,4)");
            e.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasColumnType("numeric(16,4)");
            e.Property(x => x.ChannelId).HasColumnName("channel_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.UserGroupId).HasColumnName("user_group_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.CustomerGroupId).HasColumnName("customer_group_id");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamptz");
            e.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });
    }
}
