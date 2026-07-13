using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.products.create</c> — inserts the <c>catalog_products</c> base row and syncs the simple
/// nested associations (categoryIds, tags). The CRUD factory indexes the product into the query index
/// on success (<c>catalog:catalog_product</c>) and emits <c>catalog.product.created</c>. Undoable (undo
/// soft-deletes the row). Returns <c>{ id }</c> (upstream 201 shape uses <c>result.productId ?? id</c>).
///
/// PARITY-TODO (later slices): nested <c>offers[]</c> creation, the <c>unitPrice</c> config object,
/// option-schema materialization and custom-field (<c>cf_*</c>) persistence.
/// </summary>
public sealed class CreateProductCommand
    : ICommand<ProductCreateInput, ProductResult>,
      ICommandLogMetadataBuilder<ProductCreateInput, ProductResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.products.create";

    public async Task<ProductResult> ExecuteAsync(ProductCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var product = new CatalogProduct
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Title = input.Body.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? (t.GetString()?.Trim() ?? string.Empty) : string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyProductBase(product, input.Body, isCreate: true);
        db.Set<CatalogProduct>().Add(product);
        // Persist the base product row before its assignments (ConfigureModel maps columns only, so EF
        // cannot order inserts to satisfy the DB FKs).
        await db.SaveChangesAsync();

        await CatalogWriteHelpers.SyncCategoriesAsync(db, input.Body, product.Id, input.OrganizationId, input.TenantId);
        await CatalogWriteHelpers.SyncTagsAsync(db, input.Body, product.Id, input.OrganizationId, input.TenantId);
        await db.SaveChangesAsync();

        return new ProductResult(product.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ProductCreateInput input, ProductResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create product",
        ResourceKind = "catalog.product",
        ResourceId = result.ProductId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProduct>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) p.DeletedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProduct>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) p.DeletedAt = null;
    }
}

/// <summary>
/// <c>catalog.products.update</c> — patches the base product columns (only body-present fields) and
/// re-syncs categoryIds/tags when supplied; optimistic-locked; captures before/after snapshots for the
/// changelog diff. Undoable.
/// </summary>
public sealed class UpdateProductCommand
    : ICommand<ProductUpdateInput, ProductResult>,
      ICommandLogMetadataBuilder<ProductUpdateInput, ProductResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.products.update";
    private ProductSnapshot? _before;
    private ProductSnapshot? _after;
    private ProductSnapshot? _undoBefore;

    public async Task<ProductResult> ExecuteAsync(ProductUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var product = await db.Set<CatalogProduct>().FirstOrDefaultAsync(p => p.Id == input.Id && p.DeletedAt == null);
        if (product is null) throw CommandHttpException.NotFound("Product not found");
        if (ctx.TenantId is { } tenant && product.TenantId != tenant) throw CommandHttpException.NotFound("Product not found");

        OptimisticLock.Enforce("catalog.product", product.Id.ToString(), product.UpdatedAt.UtcDateTime, ctx);

        _before = CatalogWriteHelpers.Snapshot(product);
        CatalogWriteHelpers.ApplyProductBase(product, input.Body, isCreate: false);
        product.UpdatedAt = DateTimeOffset.UtcNow;

        await CatalogWriteHelpers.SyncCategoriesAsync(db, input.Body, product.Id, product.OrganizationId, product.TenantId);
        await CatalogWriteHelpers.SyncTagsAsync(db, input.Body, product.Id, product.OrganizationId, product.TenantId);
        // Snapshot AFTER before SaveChanges — parity with the customers write path (no encrypted columns
        // on catalog today, but keep the ordering convention).
        _after = CatalogWriteHelpers.Snapshot(product);
        await db.SaveChangesAsync();

        return new ProductResult(product.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ProductUpdateInput input, ProductResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update product",
        ResourceKind = "catalog.product",
        ResourceId = result.ProductId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var product = await db.Set<CatalogProduct>().FirstOrDefaultAsync(p => p.Id == id);
        var before = log.GetSnapshotBefore<ProductSnapshot>();
        if (product is null || before is null) return;
        _undoBefore = CatalogWriteHelpers.Snapshot(product);
        RestoreSnapshot(product, before);
        product.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var product = await db.Set<CatalogProduct>().FirstOrDefaultAsync(p => p.Id == id);
        var after = log.GetSnapshotAfter<ProductSnapshot>();
        if (product is null || after is null) return;
        RestoreSnapshot(product, after);
        product.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void RestoreSnapshot(CatalogProduct p, ProductSnapshot s)
    {
        p.Title = s.Title;
        p.Subtitle = s.Subtitle;
        p.Description = s.Description;
        p.Sku = s.Sku;
        p.Handle = s.Handle;
        p.ProductType = s.ProductType;
        p.StatusEntryId = Guid.TryParse(s.StatusEntryId, out var se) ? se : null;
        p.PrimaryCurrencyCode = s.PrimaryCurrencyCode;
        p.DefaultUnit = s.DefaultUnit;
        p.DefaultSalesUnit = s.DefaultSalesUnit;
        p.DefaultSalesUnitQuantity = s.DefaultSalesUnitQuantity;
        p.UnitPriceEnabled = s.UnitPriceEnabled;
        p.UnitPriceReferenceUnit = s.UnitPriceReferenceUnit;
        p.CustomFieldsetCode = s.CustomFieldsetCode;
        p.OptionSchemaId = Guid.TryParse(s.OptionSchemaId, out var os) ? os : null;
        p.IsConfigurable = s.IsConfigurable;
        p.IsActive = s.IsActive;
    }
}

/// <summary>
/// <c>catalog.products.delete</c> — soft-deletes the product (sets <c>deleted_at</c>). The CRUD factory
/// removes it from the query index and emits <c>catalog.product.deleted</c>. Undoable.
/// </summary>
public sealed class DeleteProductCommand
    : ICommand<ProductDeleteInput, ProductResult>,
      ICommandLogMetadataBuilder<ProductDeleteInput, ProductResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.products.delete";

    public async Task<ProductResult> ExecuteAsync(ProductDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var product = await db.Set<CatalogProduct>().FirstOrDefaultAsync(p => p.Id == input.Id && p.DeletedAt == null);
        if (product is null) throw CommandHttpException.NotFound("Product not found");
        if (ctx.TenantId is { } tenant && product.TenantId != tenant) throw CommandHttpException.NotFound("Product not found");

        product.DeletedAt = DateTimeOffset.UtcNow;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new ProductResult(product.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ProductDeleteInput input, ProductResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete product",
        ResourceKind = "catalog.product",
        ResourceId = result.ProductId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProduct>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) { p.DeletedAt = null; p.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProduct>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) { p.DeletedAt = DateTimeOffset.UtcNow; p.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
