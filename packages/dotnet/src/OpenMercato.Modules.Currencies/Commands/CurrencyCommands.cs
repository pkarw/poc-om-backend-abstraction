using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Currencies.Data;

namespace OpenMercato.Modules.Currencies.Commands;

// Currency write commands — the .NET port of currencies/commands/currencies.ts. Dispatched by the
// CRUD factory through the CommandBus (which wraps ExecuteAsync in a transaction + persists the
// action-log row). All three are undoable and contribute log metadata (snapshots) for undo/redo.

/// <summary>currencies.currencies.create — reject duplicate code; demote other base currencies when base.</summary>
public sealed class CreateCurrencyCommand :
    ICommand<CurrencyCreateInput, CurrencyResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<CurrencyCreateInput, CurrencyResult>
{
    public string CommandId => "currencies.currencies.create";

    public async Task<CurrencyResult> ExecuteAsync(CurrencyCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        CurrencyValidators.NormalizeCode(input.Code, out var code);

        var exists = await db.Set<Currency>().AnyAsync(c =>
            c.Code == code && c.OrganizationId == input.OrganizationId &&
            c.TenantId == input.TenantId && c.DeletedAt == null);
        if (exists) throw CommandHttpException.Conflict("Currency code already exists for this organization.");

        var now = DateTimeOffset.UtcNow;
        var record = new Currency
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Code = code,
            Name = input.Name,
            Symbol = input.Symbol,
            DecimalPlaces = input.DecimalPlaces ?? 2,
            ThousandsSeparator = input.ThousandsSeparator,
            DecimalSeparator = input.DecimalSeparator,
            IsBase = input.IsBase ?? false,
            IsActive = input.IsActive != false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<Currency>().Add(record);

        if (record.IsBase)
            await DemoteOtherBaseAsync(db, record.Id, record.OrganizationId, record.TenantId);

        return new CurrencyResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CurrencyCreateInput input, CurrencyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create currency",
        ResourceKind = "currencies.currency",
        ResourceId = result.CurrencyId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == id);
        if (record is null) return;
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == id);
        if (record is null) return;
        record.DeletedAt = null;
        record.IsActive = true;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        if (record.IsBase)
            await DemoteOtherBaseAsync(db, record.Id, record.OrganizationId, record.TenantId);
    }

    internal static async Task DemoteOtherBaseAsync(AppDbContext db, Guid keepId, Guid orgId, Guid tenantId)
    {
        var others = await db.Set<Currency>().Where(c =>
            c.OrganizationId == orgId && c.TenantId == tenantId &&
            c.Id != keepId && c.IsBase && c.DeletedAt == null).ToListAsync();
        foreach (var other in others)
        {
            other.IsBase = false;
            other.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>currencies.currencies.update — 404 if missing; code-uniqueness; base demotion.</summary>
public sealed class UpdateCurrencyCommand :
    ICommand<CurrencyUpdateInput, CurrencyResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<CurrencyUpdateInput, CurrencyResult>
{
    public string CommandId => "currencies.currencies.update";

    private CurrencySnapshot? _before;

    public async Task<CurrencyResult> ExecuteAsync(CurrencyUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == input.Id && c.DeletedAt == null)
            ?? throw CommandHttpException.NotFound("Currency not found");
        _before = Snapshot(record);

        if (input.Code is not null)
        {
            CurrencyValidators.NormalizeCode(input.Code, out var code);
            if (code != record.Code)
            {
                var dup = await db.Set<Currency>().AnyAsync(c =>
                    c.Code == code && c.OrganizationId == record.OrganizationId &&
                    c.TenantId == record.TenantId && c.Id != record.Id && c.DeletedAt == null);
                if (dup) throw CommandHttpException.Conflict("Currency code already exists for this organization.");
                record.Code = code;
            }
        }

        if (input.Name is not null) record.Name = input.Name;
        if (input.SymbolSet) record.Symbol = input.Symbol;
        if (input.DecimalPlaces is { } dp) record.DecimalPlaces = dp;
        if (input.ThousandsSeparatorSet) record.ThousandsSeparator = input.ThousandsSeparator;
        if (input.DecimalSeparatorSet) record.DecimalSeparator = input.DecimalSeparator;
        if (input.IsBase is { } isBase) record.IsBase = isBase;
        if (input.IsActive is { } isActive) record.IsActive = isActive;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (input.IsBase == true && record.IsBase)
            await CreateCurrencyCommand.DemoteOtherBaseAsync(db, record.Id, record.OrganizationId, record.TenantId);

        return new CurrencyResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CurrencyUpdateInput input, CurrencyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update currency",
        ResourceKind = "currencies.currency",
        ResourceId = result.CurrencyId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<CurrencySnapshot>();
        if (before is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == Guid.Parse(before.Id));
        if (record is null) return;
        ApplySnapshot(record, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var input = log.GetRedoInput<CurrencyUpdateInput>();
        if (input is null) return;
        await ExecuteAsync(input, ctx, services);
    }

    internal static CurrencySnapshot Snapshot(Currency c) => new(
        c.Id.ToString(), c.OrganizationId.ToString(), c.TenantId.ToString(), c.Code, c.Name, c.Symbol,
        c.DecimalPlaces, c.ThousandsSeparator, c.DecimalSeparator, c.IsBase, c.IsActive);

    internal static void ApplySnapshot(Currency record, CurrencySnapshot s)
    {
        record.Code = s.Code;
        record.Name = s.Name;
        record.Symbol = s.Symbol;
        record.DecimalPlaces = s.DecimalPlaces;
        record.ThousandsSeparator = s.ThousandsSeparator;
        record.DecimalSeparator = s.DecimalSeparator;
        record.IsBase = s.IsBase;
        record.IsActive = s.IsActive;
        record.DeletedAt = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>currencies.currencies.delete — reject base + currencies with active rates; else soft delete.</summary>
public sealed class DeleteCurrencyCommand :
    ICommand<CurrencyDeleteInput, CurrencyResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<CurrencyDeleteInput, CurrencyResult>
{
    public string CommandId => "currencies.currencies.delete";

    private CurrencySnapshot? _before;

    public async Task<CurrencyResult> ExecuteAsync(CurrencyDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == input.Id && c.DeletedAt == null)
            ?? throw CommandHttpException.NotFound("Currency not found");

        if (record.IsBase) throw CommandHttpException.BadRequest("Cannot delete the base currency");

        var activeRates = await db.Set<ExchangeRate>().CountAsync(r =>
            (r.FromCurrencyCode == record.Code || r.ToCurrencyCode == record.Code) &&
            r.OrganizationId == record.OrganizationId && r.TenantId == record.TenantId &&
            r.DeletedAt == null && r.IsActive);
        if (activeRates > 0)
            throw CommandHttpException.BadRequest(
                $"Cannot delete currency {record.Code} because it has {activeRates} active exchange rate(s). Please delete or deactivate the exchange rates first.");

        _before = UpdateCurrencyCommand.Snapshot(record);
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
        return new CurrencyResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CurrencyDeleteInput input, CurrencyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete currency",
        ResourceKind = "currencies.currency",
        ResourceId = result.CurrencyId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<CurrencySnapshot>();
        if (before is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == Guid.Parse(before.Id));
        if (record is null) return;
        record.DeletedAt = null;
        record.IsActive = before.IsActive;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<Currency>().FirstOrDefaultAsync(c => c.Id == Guid.Parse(log.ResourceId!));
        if (record is null) return;
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
    }
}
