using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.variants.create</c> — inserts a <c>catalog_product_variants</c> row, inheriting the
/// organization/tenant scope from its parent product (upstream infers the scope from the product before
/// dispatch). The CRUD factory indexes it (<c>catalog:catalog_product_variant</c>) and emits
/// <c>catalog.variant.created</c>. Undoable (soft-delete). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreateVariantCommand
    : ICommand<VariantCreateInput, VariantResult>,
      ICommandLogMetadataBuilder<VariantCreateInput, VariantResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.variants.create";
    private Guid _org;
    private Guid _tenant;

    public async Task<VariantResult> ExecuteAsync(VariantCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var productId = CatalogHttp.GuidOf(input.Body, "productId")
            ?? throw CommandHttpException.BadRequest("productId is required");

        var product = await db.Set<CatalogProduct>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAt == null);
        if (product is null) throw CommandHttpException.NotFound("Product not found");
        if (ctx.TenantId is { } tenant && product.TenantId != tenant) throw CommandHttpException.NotFound("Product not found");

        var now = DateTimeOffset.UtcNow;
        var variant = new CatalogProductVariant
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            OrganizationId = product.OrganizationId,
            TenantId = product.TenantId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyVariantBase(variant, input.Body, isCreate: true);
        db.Set<CatalogProductVariant>().Add(variant);
        await db.SaveChangesAsync();

        _org = product.OrganizationId;
        _tenant = product.TenantId;
        return new VariantResult(variant.Id.ToString());
    }

    public CommandLogMetadata BuildLog(VariantCreateInput input, VariantResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create variant",
        ResourceKind = "catalog.variant",
        ResourceId = result.VariantId,
        TenantId = _tenant,
        OrganizationId = _org,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var v = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null) v.DeletedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var v = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null) v.DeletedAt = null;
    }
}

/// <summary><c>catalog.variants.update</c> — patches the variant base columns; optimistic-locked;
/// before/after snapshots for the changelog diff. Undoable.</summary>
public sealed class UpdateVariantCommand
    : ICommand<VariantUpdateInput, VariantResult>,
      ICommandLogMetadataBuilder<VariantUpdateInput, VariantResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.variants.update";
    private VariantSnapshot? _before;
    private VariantSnapshot? _after;

    public async Task<VariantResult> ExecuteAsync(VariantUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var variant = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(v => v.Id == input.Id && v.DeletedAt == null);
        if (variant is null) throw CommandHttpException.NotFound("Variant not found");
        if (ctx.TenantId is { } tenant && variant.TenantId != tenant) throw CommandHttpException.NotFound("Variant not found");

        OptimisticLock.Enforce("catalog.variant", variant.Id.ToString(), variant.UpdatedAt.UtcDateTime, ctx);

        _before = CatalogWriteHelpers.Snapshot(variant);
        CatalogWriteHelpers.ApplyVariantBase(variant, input.Body, isCreate: false);
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(variant);
        await db.SaveChangesAsync();
        return new VariantResult(variant.Id.ToString());
    }

    public CommandLogMetadata BuildLog(VariantUpdateInput input, VariantResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update variant",
        ResourceKind = "catalog.variant",
        ResourceId = result.VariantId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<VariantSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<VariantSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, VariantSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var v = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return;
        v.Name = s.Name; v.Sku = s.Sku; v.Barcode = s.Barcode; v.StatusEntryId = s.StatusEntryId;
        v.IsDefault = s.IsDefault; v.IsActive = s.IsActive; v.WeightUnit = s.WeightUnit;
        v.CustomFieldsetCode = s.CustomFieldsetCode;
        v.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.variants.delete</c> — soft-deletes the variant. Undoable.</summary>
public sealed class DeleteVariantCommand
    : ICommand<VariantDeleteInput, VariantResult>,
      ICommandLogMetadataBuilder<VariantDeleteInput, VariantResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.variants.delete";

    public async Task<VariantResult> ExecuteAsync(VariantDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var variant = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(v => v.Id == input.Id && v.DeletedAt == null);
        if (variant is null) throw CommandHttpException.NotFound("Variant not found");
        if (ctx.TenantId is { } tenant && variant.TenantId != tenant) throw CommandHttpException.NotFound("Variant not found");

        variant.DeletedAt = DateTimeOffset.UtcNow;
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new VariantResult(variant.Id.ToString());
    }

    public CommandLogMetadata BuildLog(VariantDeleteInput input, VariantResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete variant",
        ResourceKind = "catalog.variant",
        ResourceId = result.VariantId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var v = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null) { v.DeletedAt = null; v.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var v = await db.Set<CatalogProductVariant>().FirstOrDefaultAsync(x => x.Id == id);
        if (v is not null) { v.DeletedAt = DateTimeOffset.UtcNow; v.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
