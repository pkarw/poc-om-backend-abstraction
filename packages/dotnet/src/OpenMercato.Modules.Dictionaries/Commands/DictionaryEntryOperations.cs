using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Dictionaries.Data;

namespace OpenMercato.Modules.Dictionaries.Commands;

/// <summary>
/// <c>dictionaries.entries.reorder</c> — the port of upstream <c>commands/entry-operations.ts</c>
/// reorder. Applies the new positions in one transaction (the CommandBus wraps execute in a tx).
///
/// PARITY-TODO: undo/redo (upstream restores prior positions) — left unimplemented; the route still
/// returns <c>{ ok: true }</c> (no <c>x-om-operation</c> header without an undo token).
/// </summary>
public sealed class ReorderDictionaryEntriesCommand
    : ICommand<ReorderDictionaryEntriesInput, ReorderDictionaryEntriesResult>,
      ICommandLogMetadataBuilder<ReorderDictionaryEntriesInput, ReorderDictionaryEntriesResult>
{
    public string CommandId => "dictionaries.entries.reorder";

    public async Task<ReorderDictionaryEntriesResult> ExecuteAsync(ReorderDictionaryEntriesInput input, CommandContext ctx, IServiceProvider services)
    {
        EntryScope.Ensure(ctx, input.TenantId, input.OrganizationId);
        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
            d.Id == input.DictionaryId && d.TenantId == input.TenantId &&
            d.OrganizationId == input.OrganizationId && d.DeletedAt == null);
        if (dictionary is null) throw CommandHttpException.NotFound("Dictionary not found");

        var ids = input.Entries.Select(e => e.Id).ToList();
        var entries = await db.Set<DictionaryEntry>().Where(e =>
            ids.Contains(e.Id) && e.DictionaryId == dictionary.Id &&
            e.TenantId == input.TenantId && e.OrganizationId == input.OrganizationId).ToListAsync();
        var byId = entries.ToDictionary(e => e.Id);

        var now = DateTimeOffset.UtcNow;
        var updated = new List<string>();
        foreach (var item in input.Entries)
        {
            if (!byId.TryGetValue(item.Id, out var entry)) continue;
            entry.Position = item.Position;
            entry.UpdatedAt = now;
            updated.Add(item.Id.ToString());
        }
        return new ReorderDictionaryEntriesResult(input.DictionaryId.ToString(), updated);
    }

    public CommandLogMetadata BuildLog(ReorderDictionaryEntriesInput input, ReorderDictionaryEntriesResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Reorder dictionary entries",
        ResourceKind = "dictionaries.dictionary",
        ResourceId = result.DictionaryId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };
}

/// <summary>
/// <c>dictionaries.entries.set_default</c> — the port of upstream <c>commands/entry-operations.ts</c>
/// set-default. Clears the prior default(s) in a separate flush BEFORE setting the new default, so
/// Postgres never observes two <c>is_default=true</c> rows at once (partial unique index
/// <c>dictionary_entries_one_default_per_dict</c>).
///
/// PARITY-TODO: undo/redo (upstream restores the prior default) — left unimplemented.
/// </summary>
public sealed class SetDefaultDictionaryEntryCommand
    : ICommand<SetDefaultDictionaryEntryInput, SetDefaultDictionaryEntryResult>,
      ICommandLogMetadataBuilder<SetDefaultDictionaryEntryInput, SetDefaultDictionaryEntryResult>
{
    public string CommandId => "dictionaries.entries.set_default";

    public async Task<SetDefaultDictionaryEntryResult> ExecuteAsync(SetDefaultDictionaryEntryInput input, CommandContext ctx, IServiceProvider services)
    {
        EntryScope.Ensure(ctx, input.TenantId, input.OrganizationId);
        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
            d.Id == input.DictionaryId && d.TenantId == input.TenantId &&
            d.OrganizationId == input.OrganizationId && d.DeletedAt == null);
        if (dictionary is null) throw CommandHttpException.NotFound("Dictionary not found");

        var target = await db.Set<DictionaryEntry>().FirstOrDefaultAsync(e =>
            e.Id == input.EntryId && e.DictionaryId == dictionary.Id &&
            e.TenantId == input.TenantId && e.OrganizationId == input.OrganizationId);
        if (target is null) throw CommandHttpException.NotFound("Dictionary entry not found");

        var existingDefaults = await db.Set<DictionaryEntry>().Where(e =>
            e.DictionaryId == dictionary.Id && e.TenantId == input.TenantId &&
            e.OrganizationId == input.OrganizationId && e.IsDefault).ToListAsync();

        var now = DateTimeOffset.UtcNow;
        var cleared = new List<string>();
        foreach (var entry in existingDefaults)
        {
            if (entry.Id == target.Id) continue;
            entry.IsDefault = false;
            entry.UpdatedAt = now;
            cleared.Add(entry.Id.ToString());
        }
        // Flush the clears FIRST so the partial unique index never sees two defaults at once.
        await db.SaveChangesAsync();

        target.IsDefault = true;
        target.UpdatedAt = now;
        return new SetDefaultDictionaryEntryResult(input.DictionaryId.ToString(), target.Id.ToString(), cleared);
    }

    public CommandLogMetadata BuildLog(SetDefaultDictionaryEntryInput input, SetDefaultDictionaryEntryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Set default dictionary entry",
        ResourceKind = "dictionaries.dictionary",
        ResourceId = result.DictionaryId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };
}
