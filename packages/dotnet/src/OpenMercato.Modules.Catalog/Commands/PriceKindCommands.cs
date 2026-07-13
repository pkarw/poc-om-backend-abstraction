using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.priceKinds.create</c> — inserts a <c>catalog_price_kinds</c> row. Price kinds are
/// tenant-scoped (organization_id is nullable / tenant-global). Indexed as
/// <c>catalog:catalog_price_kind</c>. Undoable (soft-delete). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreatePriceKindCommand
    : ICommand<PriceKindCreateInput, PriceKindResult>,
      ICommandLogMetadataBuilder<PriceKindCreateInput, PriceKindResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.priceKinds.create";

    public async Task<PriceKindResult> ExecuteAsync(PriceKindCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var kind = new CatalogPriceKind
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Code = CatalogHttp.Str(input.Body, "code")?.Trim() ?? string.Empty,
            Title = CatalogHttp.Str(input.Body, "title")?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyPriceKindBase(kind, input.Body, isCreate: true);
        db.Set<CatalogPriceKind>().Add(kind);
        await db.SaveChangesAsync();
        return new PriceKindResult(kind.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceKindCreateInput input, PriceKindResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create price kind",
        ResourceKind = "catalog.price_kind",
        ResourceId = result.PriceKindId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var k = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(x => x.Id == id);
        if (k is not null) k.DeletedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var k = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(x => x.Id == id);
        if (k is not null) k.DeletedAt = null;
    }
}

/// <summary><c>catalog.priceKinds.update</c> — patches the price-kind columns; optimistic-locked;
/// before/after snapshots for the changelog diff. Undoable.</summary>
public sealed class UpdatePriceKindCommand
    : ICommand<PriceKindUpdateInput, PriceKindResult>,
      ICommandLogMetadataBuilder<PriceKindUpdateInput, PriceKindResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.priceKinds.update";
    private PriceKindSnapshot? _before;
    private PriceKindSnapshot? _after;

    public async Task<PriceKindResult> ExecuteAsync(PriceKindUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var kind = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(k => k.Id == input.Id && k.DeletedAt == null);
        if (kind is null) throw CommandHttpException.NotFound("Price kind not found");
        if (ctx.TenantId is { } tenant && kind.TenantId != tenant) throw CommandHttpException.NotFound("Price kind not found");

        OptimisticLock.Enforce("catalog.price_kind", kind.Id.ToString(), kind.UpdatedAt.UtcDateTime, ctx);

        _before = CatalogWriteHelpers.Snapshot(kind);
        CatalogWriteHelpers.ApplyPriceKindBase(kind, input.Body, isCreate: false);
        kind.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(kind);
        await db.SaveChangesAsync();
        return new PriceKindResult(kind.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceKindUpdateInput input, PriceKindResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update price kind",
        ResourceKind = "catalog.price_kind",
        ResourceId = result.PriceKindId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<PriceKindSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<PriceKindSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, PriceKindSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var k = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(x => x.Id == id);
        if (k is null) return;
        k.Code = s.Code; k.Title = s.Title; k.DisplayMode = s.DisplayMode; k.CurrencyCode = s.CurrencyCode;
        k.IsPromotion = s.IsPromotion; k.IsActive = s.IsActive;
        k.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.priceKinds.delete</c> — soft-deletes the price kind. Undoable.</summary>
public sealed class DeletePriceKindCommand
    : ICommand<PriceKindDeleteInput, PriceKindResult>,
      ICommandLogMetadataBuilder<PriceKindDeleteInput, PriceKindResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.priceKinds.delete";

    public async Task<PriceKindResult> ExecuteAsync(PriceKindDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var kind = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(k => k.Id == input.Id && k.DeletedAt == null);
        if (kind is null) throw CommandHttpException.NotFound("Price kind not found");
        if (ctx.TenantId is { } tenant && kind.TenantId != tenant) throw CommandHttpException.NotFound("Price kind not found");

        kind.DeletedAt = DateTimeOffset.UtcNow;
        kind.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new PriceKindResult(kind.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PriceKindDeleteInput input, PriceKindResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete price kind",
        ResourceKind = "catalog.price_kind",
        ResourceId = result.PriceKindId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var k = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(x => x.Id == id);
        if (k is not null) { k.DeletedAt = null; k.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var k = await db.Set<CatalogPriceKind>().FirstOrDefaultAsync(x => x.Id == id);
        if (k is not null) { k.DeletedAt = DateTimeOffset.UtcNow; k.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
