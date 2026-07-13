using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.offers.create</c> — inserts a <c>catalog_product_offers</c> row linking a product to a
/// sales channel (org/tenant from the request scope). The product must exist in scope (else 404).
/// Indexed as <c>catalog:catalog_offer</c>. Undoable (soft-delete). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreateOfferCommand
    : ICommand<OfferCreateInput, OfferResult>,
      ICommandLogMetadataBuilder<OfferCreateInput, OfferResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.offers.create";

    public async Task<OfferResult> ExecuteAsync(OfferCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var productId = CatalogHttp.GuidOf(input.Body, "productId")
            ?? throw CommandHttpException.BadRequest("productId is required");
        var productExists = await db.Set<CatalogProduct>().AnyAsync(p =>
            p.Id == productId && p.TenantId == input.TenantId && p.DeletedAt == null);
        if (!productExists) throw CommandHttpException.NotFound("Product not found");

        var now = DateTimeOffset.UtcNow;
        var offer = new CatalogOffer
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyOfferBase(offer, input.Body, isCreate: true);
        db.Set<CatalogOffer>().Add(offer);
        await db.SaveChangesAsync();
        return new OfferResult(offer.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OfferCreateInput input, OfferResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create offer",
        ResourceKind = "catalog.offer",
        ResourceId = result.OfferId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, true);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, false);

    private static async Task SetDeletedAsync(ActionLog log, IServiceProvider services, bool deleted)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var o = await db.Set<CatalogOffer>().FirstOrDefaultAsync(x => x.Id == id);
        if (o is not null) o.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
    }
}

/// <summary><c>catalog.offers.update</c> — patches the offer base columns; optimistic-locked; before/after
/// snapshots for the changelog diff. Undoable.</summary>
public sealed class UpdateOfferCommand
    : ICommand<OfferUpdateInput, OfferResult>,
      ICommandLogMetadataBuilder<OfferUpdateInput, OfferResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.offers.update";
    private OfferSnapshot? _before;
    private OfferSnapshot? _after;

    public async Task<OfferResult> ExecuteAsync(OfferUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var offer = await db.Set<CatalogOffer>().FirstOrDefaultAsync(o => o.Id == input.Id && o.DeletedAt == null);
        if (offer is null) throw CommandHttpException.NotFound("Offer not found");
        if (ctx.TenantId is { } tenant && offer.TenantId != tenant) throw CommandHttpException.NotFound("Offer not found");

        OptimisticLock.Enforce("catalog.offer", offer.Id.ToString(), offer.UpdatedAt.UtcDateTime, ctx);
        _before = CatalogWriteHelpers.Snapshot(offer);
        CatalogWriteHelpers.ApplyOfferBase(offer, input.Body, isCreate: false);
        offer.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(offer);
        await db.SaveChangesAsync();
        return new OfferResult(offer.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OfferUpdateInput input, OfferResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update offer",
        ResourceKind = "catalog.offer",
        ResourceId = result.OfferId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<OfferSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<OfferSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, OfferSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var o = await db.Set<CatalogOffer>().FirstOrDefaultAsync(x => x.Id == id);
        if (o is null) return;
        o.Title = s.Title; o.Description = s.Description;
        o.DefaultMediaId = Guid.TryParse(s.DefaultMediaId, out var m) ? m : null;
        o.DefaultMediaUrl = s.DefaultMediaUrl; o.IsActive = s.IsActive;
        o.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.offers.delete</c> — soft-deletes the offer. Undoable.</summary>
public sealed class DeleteOfferCommand
    : ICommand<OfferDeleteInput, OfferResult>,
      ICommandLogMetadataBuilder<OfferDeleteInput, OfferResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.offers.delete";

    public async Task<OfferResult> ExecuteAsync(OfferDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var offer = await db.Set<CatalogOffer>().FirstOrDefaultAsync(o => o.Id == input.Id && o.DeletedAt == null);
        if (offer is null) throw CommandHttpException.NotFound("Offer not found");
        if (ctx.TenantId is { } tenant && offer.TenantId != tenant) throw CommandHttpException.NotFound("Offer not found");

        offer.DeletedAt = DateTimeOffset.UtcNow;
        offer.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new OfferResult(offer.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OfferDeleteInput input, OfferResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete offer",
        ResourceKind = "catalog.offer",
        ResourceId = result.OfferId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var o = await db.Set<CatalogOffer>().FirstOrDefaultAsync(x => x.Id == id);
        if (o is not null) { o.DeletedAt = null; o.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var o = await db.Set<CatalogOffer>().FirstOrDefaultAsync(x => x.Id == id);
        if (o is not null) { o.DeletedAt = DateTimeOffset.UtcNow; o.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
