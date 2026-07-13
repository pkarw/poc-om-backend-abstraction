using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.product-unit-conversions.create</c> — inserts a <c>catalog_product_unit_conversions</c> row
/// (an alternate-unit → base-unit factor for a product). The product must exist in scope (404). Indexed
/// as <c>catalog:catalog_product_unit_conversion</c>. Undoable (soft-delete). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreateUnitConversionCommand
    : ICommand<UnitConversionCreateInput, UnitConversionResult>,
      ICommandLogMetadataBuilder<UnitConversionCreateInput, UnitConversionResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.product-unit-conversions.create";

    public async Task<UnitConversionResult> ExecuteAsync(UnitConversionCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var productId = CatalogHttp.GuidOf(input.Body, "productId")
            ?? throw CommandHttpException.BadRequest("productId is required");
        var productExists = await db.Set<CatalogProduct>().AnyAsync(p =>
            p.Id == productId && p.TenantId == input.TenantId && p.DeletedAt == null);
        if (!productExists) throw CommandHttpException.NotFound("Product not found");

        var now = DateTimeOffset.UtcNow;
        var conversion = new CatalogProductUnitConversion
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            UnitCode = CatalogHttp.Str(input.Body, "unitCode")?.Trim() ?? string.Empty,
            ToBaseFactor = CatalogHttp.Decimal(input.Body, "toBaseFactor") ?? 0m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyUnitConversionBase(conversion, input.Body, isCreate: true);
        db.Set<CatalogProductUnitConversion>().Add(conversion);
        await db.SaveChangesAsync();
        return new UnitConversionResult(conversion.Id.ToString());
    }

    public CommandLogMetadata BuildLog(UnitConversionCreateInput input, UnitConversionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create product unit conversion",
        ResourceKind = "catalog.product_unit_conversion",
        ResourceId = result.ConversionId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, true);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, false);

    private static async Task SetDeletedAsync(ActionLog log, IServiceProvider services, bool deleted)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var u = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(x => x.Id == id);
        if (u is not null) u.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
    }
}

/// <summary><c>catalog.product-unit-conversions.update</c> — patches the conversion; optimistic-locked;
/// before/after snapshots. Undoable.</summary>
public sealed class UpdateUnitConversionCommand
    : ICommand<UnitConversionUpdateInput, UnitConversionResult>,
      ICommandLogMetadataBuilder<UnitConversionUpdateInput, UnitConversionResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.product-unit-conversions.update";
    private UnitConversionSnapshot? _before;
    private UnitConversionSnapshot? _after;

    public async Task<UnitConversionResult> ExecuteAsync(UnitConversionUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var conversion = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(u => u.Id == input.Id && u.DeletedAt == null);
        if (conversion is null) throw CommandHttpException.NotFound("Product unit conversion not found");
        if (ctx.TenantId is { } tenant && conversion.TenantId != tenant) throw CommandHttpException.NotFound("Product unit conversion not found");

        OptimisticLock.Enforce("catalog.product_unit_conversion", conversion.Id.ToString(), conversion.UpdatedAt.UtcDateTime, ctx);
        _before = CatalogWriteHelpers.Snapshot(conversion);
        CatalogWriteHelpers.ApplyUnitConversionBase(conversion, input.Body, isCreate: false);
        conversion.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(conversion);
        await db.SaveChangesAsync();
        return new UnitConversionResult(conversion.Id.ToString());
    }

    public CommandLogMetadata BuildLog(UnitConversionUpdateInput input, UnitConversionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update product unit conversion",
        ResourceKind = "catalog.product_unit_conversion",
        ResourceId = result.ConversionId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<UnitConversionSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<UnitConversionSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, UnitConversionSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var u = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(x => x.Id == id);
        if (u is null) return;
        u.UnitCode = s.UnitCode; u.ToBaseFactor = s.ToBaseFactor; u.SortOrder = s.SortOrder; u.IsActive = s.IsActive;
        u.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.product-unit-conversions.delete</c> — soft-deletes the conversion. Undoable.</summary>
public sealed class DeleteUnitConversionCommand
    : ICommand<UnitConversionDeleteInput, UnitConversionResult>,
      ICommandLogMetadataBuilder<UnitConversionDeleteInput, UnitConversionResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.product-unit-conversions.delete";

    public async Task<UnitConversionResult> ExecuteAsync(UnitConversionDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var conversion = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(u => u.Id == input.Id && u.DeletedAt == null);
        if (conversion is null) throw CommandHttpException.NotFound("Product unit conversion not found");
        if (ctx.TenantId is { } tenant && conversion.TenantId != tenant) throw CommandHttpException.NotFound("Product unit conversion not found");

        conversion.DeletedAt = DateTimeOffset.UtcNow;
        conversion.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new UnitConversionResult(conversion.Id.ToString());
    }

    public CommandLogMetadata BuildLog(UnitConversionDeleteInput input, UnitConversionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete product unit conversion",
        ResourceKind = "catalog.product_unit_conversion",
        ResourceId = result.ConversionId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var u = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(x => x.Id == id);
        if (u is not null) { u.DeletedAt = null; u.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var u = await db.Set<CatalogProductUnitConversion>().FirstOrDefaultAsync(x => x.Id == id);
        if (u is not null) { u.DeletedAt = DateTimeOffset.UtcNow; u.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
