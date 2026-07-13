using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.prices.create</c> — inserts a <c>catalog_product_variant_prices</c> row (org/tenant from
/// the request scope). Indexed as <c>catalog:catalog_product_price</c>. The table has NO soft-delete, so
/// undo hard-deletes and redo re-inserts from the created snapshot. Returns <c>{ id }</c>.
/// </summary>
public sealed class CreatePriceCommand
    : ICommand<PriceCreateInput, PriceResult>,
      ICommandLogMetadataBuilder<PriceCreateInput, PriceResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.prices.create";
    private PriceSnapshot? _after;

    public async Task<PriceResult> ExecuteAsync(PriceCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var price = new CatalogProductPrice
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            PriceKindId = CatalogHttp.GuidOf(input.Body, "priceKindId") ?? Guid.Empty,
            CurrencyCode = CatalogHttp.Str(input.Body, "currencyCode")?.Trim().ToUpperInvariant() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyPriceBase(price, input.Body, isCreate: true);
        db.Set<CatalogProductPrice>().Add(price);
        await db.SaveChangesAsync();
        _after = CatalogWriteHelpers.Snapshot(price);
        return new PriceResult(price.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceCreateInput input, PriceResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create price",
        ResourceKind = "catalog.price",
        ResourceId = result.PriceId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProductPrice>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) db.Set<CatalogProductPrice>().Remove(p);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var snap = log.GetSnapshotAfter<PriceSnapshot>();
        var exists = await db.Set<CatalogProductPrice>().AnyAsync(x => x.Id == id);
        if (snap is null || exists) return;
        db.Set<CatalogProductPrice>().Add(CatalogWriteHelpers.FromSnapshot(id, log.OrganizationId ?? Guid.Empty, log.TenantId ?? Guid.Empty, snap));
    }
}

/// <summary><c>catalog.prices.update</c> — patches the price columns; before/after snapshots for the
/// changelog diff. Undoable.</summary>
public sealed class UpdatePriceCommand
    : ICommand<PriceUpdateInput, PriceResult>,
      ICommandLogMetadataBuilder<PriceUpdateInput, PriceResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.prices.update";
    private PriceSnapshot? _before;
    private PriceSnapshot? _after;

    public async Task<PriceResult> ExecuteAsync(PriceUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var price = await db.Set<CatalogProductPrice>().FirstOrDefaultAsync(p => p.Id == input.Id);
        if (price is null) throw CommandHttpException.NotFound("Price not found");
        if (ctx.TenantId is { } tenant && price.TenantId != tenant) throw CommandHttpException.NotFound("Price not found");

        OptimisticLock.Enforce("catalog.price", price.Id.ToString(), price.UpdatedAt.UtcDateTime, ctx);

        _before = CatalogWriteHelpers.Snapshot(price);
        CatalogWriteHelpers.ApplyPriceBase(price, input.Body, isCreate: false);
        price.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(price);
        await db.SaveChangesAsync();
        return new PriceResult(price.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceUpdateInput input, PriceResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update price",
        ResourceKind = "catalog.price",
        ResourceId = result.PriceId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<PriceSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<PriceSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, PriceSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProductPrice>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return;
        var restored = CatalogWriteHelpers.FromSnapshot(id, p.OrganizationId, p.TenantId, s);
        p.ProductId = restored.ProductId; p.VariantId = restored.VariantId; p.OfferId = restored.OfferId;
        p.PriceKindId = restored.PriceKindId; p.CurrencyCode = restored.CurrencyCode; p.Kind = restored.Kind;
        p.MinQuantity = restored.MinQuantity; p.MaxQuantity = restored.MaxQuantity;
        p.UnitPriceNet = restored.UnitPriceNet; p.UnitPriceGross = restored.UnitPriceGross;
        p.TaxRate = restored.TaxRate; p.TaxAmount = restored.TaxAmount; p.ChannelId = restored.ChannelId;
        p.UserId = restored.UserId; p.UserGroupId = restored.UserGroupId; p.CustomerId = restored.CustomerId;
        p.CustomerGroupId = restored.CustomerGroupId; p.Metadata = restored.Metadata;
        p.StartsAt = restored.StartsAt; p.EndsAt = restored.EndsAt;
        p.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.prices.delete</c> — HARD-deletes the price row (no soft-delete column). Undoable
/// by re-inserting from the captured snapshot.</summary>
public sealed class DeletePriceCommand
    : ICommand<PriceDeleteInput, PriceResult>,
      ICommandLogMetadataBuilder<PriceDeleteInput, PriceResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.prices.delete";
    private PriceSnapshot? _before;
    private Guid _org;
    private Guid _tenant;

    public async Task<PriceResult> ExecuteAsync(PriceDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var price = await db.Set<CatalogProductPrice>().FirstOrDefaultAsync(p => p.Id == input.Id);
        if (price is null) throw CommandHttpException.NotFound("Price not found");
        if (ctx.TenantId is { } tenant && price.TenantId != tenant) throw CommandHttpException.NotFound("Price not found");

        _before = CatalogWriteHelpers.Snapshot(price);
        _org = price.OrganizationId;
        _tenant = price.TenantId;
        db.Set<CatalogProductPrice>().Remove(price);
        await db.SaveChangesAsync();
        return new PriceResult(input.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceDeleteInput input, PriceResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete price",
        ResourceKind = "catalog.price",
        ResourceId = result.PriceId,
        TenantId = _tenant,
        OrganizationId = _org,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var snap = log.GetSnapshotBefore<PriceSnapshot>();
        var exists = await db.Set<CatalogProductPrice>().AnyAsync(x => x.Id == id);
        if (snap is null || exists) return;
        db.Set<CatalogProductPrice>().Add(CatalogWriteHelpers.FromSnapshot(id, log.OrganizationId ?? Guid.Empty, log.TenantId ?? Guid.Empty, snap));
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var p = await db.Set<CatalogProductPrice>().FirstOrDefaultAsync(x => x.Id == id);
        if (p is not null) db.Set<CatalogProductPrice>().Remove(p);
    }
}
