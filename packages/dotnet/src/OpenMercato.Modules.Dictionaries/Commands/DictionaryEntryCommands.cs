using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Dictionaries.Data;
using OpenMercato.Modules.Dictionaries.Lib;

namespace OpenMercato.Modules.Dictionaries.Commands;

/// <summary>Scope guard shared by entry commands — the port of upstream <c>ensureScope</c>
/// (commands/entries.ts). Rejects cross-tenant/org writes with 403.</summary>
internal static class EntryScope
{
    public static void Ensure(CommandContext ctx, Guid tenantId, Guid organizationId)
    {
        if (ctx.TenantId is { } t && t != tenantId) throw CommandHttpException.Forbidden();
        var org = ctx.OrganizationId;
        if (org is { } o && o != organizationId) throw CommandHttpException.Forbidden();
    }

    public static DictionaryEntrySnapshot Snapshot(DictionaryEntry e) => new(
        e.Id, e.DictionaryId, e.OrganizationId, e.TenantId, e.Value, e.Label, e.Color, e.Icon,
        e.Position, e.IsDefault, e.CreatedAt, e.UpdatedAt);
}

/// <summary>
/// <c>dictionaries.entries.create</c> — the port of the create handler in upstream
/// <c>commands/factory.ts</c>. Normalizes value, sanitizes color/icon, defaults label to value,
/// rejects duplicate normalized values (409). Undoable (undo removes the row).
/// </summary>
public sealed class CreateDictionaryEntryCommand
    : ICommand<DictionaryEntryCreateInput, DictionaryEntryResult>,
      ICommandLogMetadataBuilder<DictionaryEntryCreateInput, DictionaryEntryResult>,
      IUndoableCommand
{
    public string CommandId => "dictionaries.entries.create";

    private DictionaryEntrySnapshot? _after;

    public async Task<DictionaryEntryResult> ExecuteAsync(DictionaryEntryCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d => d.Id == input.DictionaryId && d.DeletedAt == null);
        if (dictionary is null) throw CommandHttpException.NotFound("Dictionary not found");
        EntryScope.Ensure(ctx, dictionary.TenantId, dictionary.OrganizationId);

        var value = input.Value.Trim();
        if (value.Length == 0) throw CommandHttpException.BadRequest("Dictionary entry value is required.");
        var normalized = DictionaryUtils.NormalizeValue(value);
        var label = string.IsNullOrWhiteSpace(input.Label) ? value : input.Label!.Trim();

        var duplicate = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e =>
            e.DictionaryId == dictionary.Id && e.TenantId == dictionary.TenantId &&
            e.OrganizationId == dictionary.OrganizationId && e.NormalizedValue == normalized);
        if (duplicate is not null) throw CommandHttpException.Conflict("An entry with this value already exists.");

        var now = DateTimeOffset.UtcNow;
        var entry = new DictionaryEntry
        {
            Id = Guid.NewGuid(),
            DictionaryId = dictionary.Id,
            TenantId = dictionary.TenantId,
            OrganizationId = dictionary.OrganizationId,
            Value = value,
            NormalizedValue = normalized,
            Label = label,
            Color = DictionaryUtils.SanitizeColor(input.Color),
            Icon = DictionaryUtils.SanitizeIcon(input.Icon),
            Position = input.Position ?? 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<DictionaryEntry>().Add(entry);
        _after = EntryScope.Snapshot(entry);
        return new DictionaryEntryResult(entry.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DictionaryEntryCreateInput input, DictionaryEntryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create dictionary entry",
        ResourceKind = "dictionaries.entry",
        ResourceId = result.EntryId,
        TenantId = _after?.TenantId,
        OrganizationId = _after?.OrganizationId,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var entry = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e => e.Id == id);
        if (entry is not null) db.Set<DictionaryEntry>().Remove(entry);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var after = log.GetSnapshotAfter<DictionaryEntrySnapshot>();
        if (after is null) return;
        await EntryUndo.Restore(services, after);
    }
}

/// <summary>
/// <c>dictionaries.entries.update</c> — the port of the update handler in upstream
/// <c>commands/factory.ts</c>. Applies only provided fields; value change re-normalizes + dup-checks
/// (409) and re-defaults label; optimistic-locked. Undoable (restores the before-snapshot).
/// </summary>
public sealed class UpdateDictionaryEntryCommand
    : ICommand<DictionaryEntryUpdateInput, DictionaryEntryResult>,
      ICommandLogMetadataBuilder<DictionaryEntryUpdateInput, DictionaryEntryResult>,
      IUndoableCommand
{
    public string CommandId => "dictionaries.entries.update";

    private DictionaryEntrySnapshot? _before;
    private DictionaryEntrySnapshot? _after;

    public async Task<DictionaryEntryResult> ExecuteAsync(DictionaryEntryUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entry = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e => e.Id == input.Id);
        if (entry is null) throw CommandHttpException.NotFound("Dictionary entry not found");
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d => d.Id == entry.DictionaryId);
        if (dictionary is null || dictionary.DeletedAt != null) throw CommandHttpException.NotFound("Dictionary not found");
        EntryScope.Ensure(ctx, entry.TenantId, entry.OrganizationId);

        OptimisticLock.Enforce("dictionaries.entry", entry.Id.ToString(), entry.UpdatedAt.UtcDateTime, ctx);
        _before = EntryScope.Snapshot(entry);

        if (input.Provided.Contains("value") && input.Value is not null)
        {
            var value = input.Value.Trim();
            if (value.Length == 0) throw CommandHttpException.BadRequest("Dictionary entry value is required.");
            var normalized = DictionaryUtils.NormalizeValue(value);
            if (normalized != entry.NormalizedValue)
            {
                var dup = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e =>
                    e.DictionaryId == entry.DictionaryId && e.TenantId == entry.TenantId &&
                    e.OrganizationId == entry.OrganizationId && e.NormalizedValue == normalized && e.Id != entry.Id);
                if (dup is not null) throw CommandHttpException.Conflict("An entry with this value already exists.");
                entry.Value = value;
                entry.NormalizedValue = normalized;
                if (!input.Provided.Contains("label")) entry.Label = entry.Value;
            }
        }
        if (input.Provided.Contains("label") && input.Label is not null)
            entry.Label = input.Label.Trim().Length > 0 ? input.Label.Trim() : entry.Value;
        if (input.Provided.Contains("color"))
            entry.Color = DictionaryUtils.SanitizeColor(input.Color);
        if (input.Provided.Contains("icon"))
            entry.Icon = DictionaryUtils.SanitizeIcon(input.Icon);
        if (input.Provided.Contains("position") && input.Position is { } pos)
            entry.Position = pos;
        if (input.Provided.Contains("isDefault") && input.IsDefault is { } def)
            entry.IsDefault = def;

        entry.UpdatedAt = DateTimeOffset.UtcNow;
        _after = EntryScope.Snapshot(entry);
        return new DictionaryEntryResult(entry.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DictionaryEntryUpdateInput input, DictionaryEntryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update dictionary entry",
        ResourceKind = "dictionaries.entry",
        ResourceId = result.EntryId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        if (before is not null) await EntryUndo.Restore(services, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var after = log.GetSnapshotAfter<DictionaryEntrySnapshot>();
        if (after is not null) await EntryUndo.Restore(services, after);
    }
}

/// <summary>
/// <c>dictionaries.entries.delete</c> — the port of the delete handler in upstream
/// <c>commands/factory.ts</c>. Hard-deletes the entry row. Undoable (recreates from the before-snapshot).
/// </summary>
public sealed class DeleteDictionaryEntryCommand
    : ICommand<DictionaryEntryDeleteInput, DictionaryEntryResult>,
      ICommandLogMetadataBuilder<DictionaryEntryDeleteInput, DictionaryEntryResult>,
      IUndoableCommand
{
    public string CommandId => "dictionaries.entries.delete";

    private DictionaryEntrySnapshot? _before;

    public async Task<DictionaryEntryResult> ExecuteAsync(DictionaryEntryDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entry = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e => e.Id == input.Id);
        if (entry is null) throw CommandHttpException.NotFound("Dictionary entry not found");
        EntryScope.Ensure(ctx, entry.TenantId, entry.OrganizationId);
        _before = EntryScope.Snapshot(entry);
        db.Set<DictionaryEntry>().Remove(entry);
        return new DictionaryEntryResult(entry.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DictionaryEntryDeleteInput input, DictionaryEntryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete dictionary entry",
        ResourceKind = "dictionaries.entry",
        ResourceId = result.EntryId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        if (before is not null) await EntryUndo.Restore(services, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var entry = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e => e.Id == id);
        if (entry is not null) db.Set<DictionaryEntry>().Remove(entry);
    }
}

/// <summary>Re-materialize an entry from a snapshot, reusing its original id (undo/redo invariant).</summary>
internal static class EntryUndo
{
    public static async Task Restore(IServiceProvider services, DictionaryEntrySnapshot snap)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d => d.Id == snap.DictionaryId);
        if (dictionary is null) return;
        var entry = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e => e.Id == snap.Id);
        if (entry is null)
        {
            entry = new DictionaryEntry { Id = snap.Id, DictionaryId = snap.DictionaryId };
            db.Set<DictionaryEntry>().Add(entry);
        }
        entry.DictionaryId = snap.DictionaryId;
        entry.OrganizationId = snap.OrganizationId;
        entry.TenantId = snap.TenantId;
        entry.Value = snap.Value;
        entry.NormalizedValue = DictionaryUtils.NormalizeValue(snap.Value);
        entry.Label = snap.Label;
        entry.Color = snap.Color;
        entry.Icon = snap.Icon;
        entry.Position = snap.Position;
        entry.IsDefault = snap.IsDefault;
        entry.CreatedAt = snap.CreatedAt;
        entry.UpdatedAt = snap.UpdatedAt;
    }
}
