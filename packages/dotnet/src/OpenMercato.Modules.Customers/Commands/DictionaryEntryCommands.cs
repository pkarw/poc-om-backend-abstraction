using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// Customer dictionary-entry write commands — the port of upstream <c>commands/dictionaries.ts</c>.
/// Create is an upsert keyed on (tenant, org, kind, normalized_value) returning a
/// <c>created|updated|unchanged</c> mode (drives the route's 201-vs-200); update/delete enforce the
/// <c>person_company_role</c> in-use guard (409 <c>role_type_in_use</c>). All three are undoable via
/// before/after snapshots. Color/icon honour the tri-state body semantics (absent = keep, null = clear).
/// </summary>
internal static class DictionaryEntryReader
{
    /// <summary>Tri-state read of a nullable-optional appearance field.</summary>
    public static (bool Present, string? Value) Field(JsonElement body, string name, Func<string?, string?> normalize)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var el)) return (false, null);
        if (el.ValueKind == JsonValueKind.Null) return (true, null);
        return (true, normalize(el.ValueKind == JsonValueKind.String ? el.GetString() : null));
    }

    public static (bool Present, string? Raw) Label(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("label", out var el)) return (false, null);
        return (true, el.ValueKind == JsonValueKind.String ? el.GetString() : null);
    }

    public static DictionaryEntrySnapshot Snapshot(CustomerDictionaryEntry e) => new(
        e.Id, e.OrganizationId, e.TenantId, e.Kind, e.Value, e.NormalizedValue, e.Label, e.Color, e.Icon, e.CreatedAt, e.UpdatedAt);

    public static void Apply(CustomerDictionaryEntry e, DictionaryEntrySnapshot s)
    {
        e.OrganizationId = s.OrganizationId; e.TenantId = s.TenantId; e.Kind = s.Kind;
        e.Value = s.Value; e.NormalizedValue = s.NormalizedValue; e.Label = s.Label;
        e.Color = s.Color; e.Icon = s.Icon; e.CreatedAt = s.CreatedAt; e.UpdatedAt = s.UpdatedAt;
    }

    public static CommandHttpException RoleTypeInUse(DictionaryContext.RoleTypeUsage usage) =>
        new(409, new Dictionary<string, object?>
        {
            ["code"] = "role_type_in_use",
            ["error"] = "Role type is in use",
            ["usageCount"] = usage.Total,
            ["ownerAssignments"] = usage.OwnerAssignments,
            ["relationshipAssignments"] = usage.RelationshipAssignments,
        });
}

/// <summary><c>customers.dictionaryEntries.create</c> — upsert by (tenant,org,kind,normalizedValue).</summary>
public sealed class CreateDictionaryEntryCommand
    : ICommand<DictionaryEntryCreateInput, DictionaryEntryWriteResult>,
      ICommandLogMetadataBuilder<DictionaryEntryCreateInput, DictionaryEntryWriteResult>, IUndoableCommand
{
    public string CommandId => "customers.dictionaryEntries.create";
    private DictionaryEntrySnapshot? _before, _after;
    private string _mode = "unchanged";

    public async Task<DictionaryEntryWriteResult> ExecuteAsync(DictionaryEntryCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var rawValue = (CustomersHttp.Str(body, "value") ?? string.Empty).Trim();
        if (rawValue.Length == 0) throw CommandHttpException.BadRequest("Dictionary entry value is required");
        var normalized = rawValue.ToLowerInvariant();
        var (labelPresent, labelRaw) = DictionaryEntryReader.Label(body);
        var computedLabel = (labelRaw?.Trim() is { Length: > 0 } t) ? t : rawValue;
        var (colorPresent, color) = DictionaryEntryReader.Field(body, "color", DictionaryContext.NormalizeColor);
        var (iconPresent, icon) = DictionaryEntryReader.Field(body, "icon", DictionaryContext.NormalizeIcon);

        var existing = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e =>
            e.TenantId == input.TenantId && e.OrganizationId == input.OrganizationId &&
            e.Kind == input.Kind && e.NormalizedValue == normalized);

        if (existing is not null)
        {
            _before = DictionaryEntryReader.Snapshot(existing);
            var changed = false;
            if (labelPresent && existing.Label != computedLabel) { existing.Label = computedLabel; changed = true; }
            if (colorPresent && existing.Color != color) { existing.Color = color; changed = true; }
            if (iconPresent && existing.Icon != icon) { existing.Icon = icon; changed = true; }
            if (changed) existing.UpdatedAt = DateTimeOffset.UtcNow;
            _mode = changed ? "updated" : "unchanged";
            _after = DictionaryEntryReader.Snapshot(existing);
            return new DictionaryEntryWriteResult(existing.Id.ToString(), existing.OrganizationId, _mode, existing.Value, existing.Label, existing.Color, existing.Icon);
        }

        var now = DateTimeOffset.UtcNow;
        var entry = new CustomerDictionaryEntry
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId, Kind = input.Kind,
            Value = rawValue, NormalizedValue = normalized, Label = computedLabel, Color = color, Icon = icon,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerDictionaryEntry>().Add(entry);
        _mode = "created";
        _after = DictionaryEntryReader.Snapshot(entry);
        return new DictionaryEntryWriteResult(entry.Id.ToString(), entry.OrganizationId, _mode, entry.Value, entry.Label, entry.Color, entry.Icon);
    }

    public CommandLogMetadata? BuildLog(DictionaryEntryCreateInput input, DictionaryEntryWriteResult result, CommandContext ctx)
    {
        if (_mode == "unchanged" || _after is null) return new CommandLogMetadata { SkipLog = true };
        return new CommandLogMetadata
        {
            ActionLabel = _mode == "created" ? "Add dictionary entry" : "Update dictionary entry",
            ResourceKind = "customers.dictionary_entry", ResourceId = result.EntryId,
            TenantId = _after.TenantId, OrganizationId = _after.OrganizationId,
            SnapshotBefore = _before, SnapshotAfter = _after,
        };
    }

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        var after = log.GetSnapshotAfter<DictionaryEntrySnapshot>();
        var id = Guid.Parse(log.ResourceId!);
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == id);
        if (before is null)
        {
            if (entry is not null) db.Set<CustomerDictionaryEntry>().Remove(entry);
            return;
        }
        if (entry is null) { entry = new CustomerDictionaryEntry { Id = before.Id }; db.Set<CustomerDictionaryEntry>().Add(entry); }
        DictionaryEntryReader.Apply(entry, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<DictionaryEntrySnapshot>();
        if (after is null) return;
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == after.Id);
        if (entry is null) { entry = new CustomerDictionaryEntry { Id = after.Id }; db.Set<CustomerDictionaryEntry>().Add(entry); }
        DictionaryEntryReader.Apply(entry, after);
        entry.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>customers.dictionaryEntries.update</c> — 404 missing; 409 role_type_in_use / duplicate.</summary>
public sealed class UpdateDictionaryEntryCommand
    : ICommand<DictionaryEntryUpdateInput, DictionaryEntryUpdateResult>,
      ICommandLogMetadataBuilder<DictionaryEntryUpdateInput, DictionaryEntryUpdateResult>, IUndoableCommand
{
    public string CommandId => "customers.dictionaryEntries.update";
    private DictionaryEntrySnapshot? _before, _after;
    private bool _changed;

    public async Task<DictionaryEntryUpdateResult> ExecuteAsync(DictionaryEntryUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == input.Id);
        if (entry is null || entry.OrganizationId != input.OrganizationId || entry.TenantId != input.TenantId || entry.Kind != input.Kind)
            throw CommandHttpException.NotFound("Dictionary entry not found");
        _before = DictionaryEntryReader.Snapshot(entry);

        var body = input.Body;
        var valuePresent = CustomersHttp.Has(body, "value");
        var (labelPresent, labelRaw) = DictionaryEntryReader.Label(body);
        var (colorPresent, color) = DictionaryEntryReader.Field(body, "color", DictionaryContext.NormalizeColor);
        var (iconPresent, icon) = DictionaryEntryReader.Field(body, "icon", DictionaryContext.NormalizeIcon);

        if (valuePresent)
        {
            var rawValue = (CustomersHttp.Str(body, "value") ?? string.Empty).Trim();
            if (rawValue.Length == 0) throw CommandHttpException.BadRequest("Dictionary entry value is required");
            var normalized = rawValue.ToLowerInvariant();
            if (normalized != entry.NormalizedValue)
            {
                if (entry.Kind == "person_company_role")
                {
                    var usage = await DictionaryContext.LoadRoleTypeUsageAsync(db, input.TenantId, entry.OrganizationId, entry.Value);
                    if (usage.Total > 0) throw DictionaryEntryReader.RoleTypeInUse(usage);
                }
                var dup = await db.Set<CustomerDictionaryEntry>().AnyAsync(e =>
                    e.Id != entry.Id && e.TenantId == input.TenantId && e.OrganizationId == input.OrganizationId &&
                    e.Kind == input.Kind && e.NormalizedValue == normalized);
                if (dup) throw CommandHttpException.Conflict("An entry with this value already exists");
                entry.Value = rawValue; entry.NormalizedValue = normalized;
                if (!labelPresent) entry.Label = rawValue;
                _changed = true;
            }
        }

        if (labelPresent)
        {
            var label = labelRaw?.Trim() is { Length: > 0 } t ? t : entry.Value;
            if (entry.Label != label) { entry.Label = label; _changed = true; }
        }
        if (colorPresent && entry.Color != color) { entry.Color = color; _changed = true; }
        if (iconPresent && entry.Icon != icon) { entry.Icon = icon; _changed = true; }

        if (_changed) entry.UpdatedAt = DateTimeOffset.UtcNow;
        _after = DictionaryEntryReader.Snapshot(entry);
        return new DictionaryEntryUpdateResult(entry.Id.ToString(), _changed, entry.Value, entry.Label, entry.Color, entry.Icon);
    }

    public CommandLogMetadata? BuildLog(DictionaryEntryUpdateInput input, DictionaryEntryUpdateResult result, CommandContext ctx)
    {
        if (!_changed || _before is null || _after is null) return new CommandLogMetadata { SkipLog = true };
        return new CommandLogMetadata
        {
            ActionLabel = "Update dictionary entry", ResourceKind = "customers.dictionary_entry", ResourceId = result.EntryId,
            TenantId = _after.TenantId, OrganizationId = _after.OrganizationId, SnapshotBefore = _before, SnapshotAfter = _after,
        };
    }

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        if (before is null) return;
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == before.Id);
        if (entry is null) { entry = new CustomerDictionaryEntry { Id = before.Id }; db.Set<CustomerDictionaryEntry>().Add(entry); }
        DictionaryEntryReader.Apply(entry, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<DictionaryEntrySnapshot>();
        if (after is null) return;
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == after.Id);
        if (entry is not null) DictionaryEntryReader.Apply(entry, after);
    }
}

/// <summary><c>customers.dictionaryEntries.delete</c> — 404 missing; 409 role_type_in_use.</summary>
public sealed class DeleteDictionaryEntryCommand
    : ICommand<DictionaryEntryDeleteInput, DictionaryEntryDeleteResult>,
      ICommandLogMetadataBuilder<DictionaryEntryDeleteInput, DictionaryEntryDeleteResult>, IUndoableCommand
{
    public string CommandId => "customers.dictionaryEntries.delete";
    private DictionaryEntrySnapshot? _before;

    public async Task<DictionaryEntryDeleteResult> ExecuteAsync(DictionaryEntryDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == input.Id);
        if (entry is null || entry.OrganizationId != input.OrganizationId || entry.TenantId != input.TenantId || entry.Kind != input.Kind)
            throw CommandHttpException.NotFound("Dictionary entry not found");
        if (entry.Kind == "person_company_role")
        {
            var usage = await DictionaryContext.LoadRoleTypeUsageAsync(db, entry.TenantId, entry.OrganizationId, entry.Value);
            if (usage.Total > 0) throw DictionaryEntryReader.RoleTypeInUse(usage);
        }
        _before = DictionaryEntryReader.Snapshot(entry);
        db.Set<CustomerDictionaryEntry>().Remove(entry);
        return new DictionaryEntryDeleteResult(entry.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DictionaryEntryDeleteInput input, DictionaryEntryDeleteResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete dictionary entry", ResourceKind = "customers.dictionary_entry", ResourceId = result.EntryId,
        TenantId = _before?.TenantId, OrganizationId = _before?.OrganizationId, SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        if (before is null) return;
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == before.Id);
        if (entry is null) { entry = new CustomerDictionaryEntry { Id = before.Id }; db.Set<CustomerDictionaryEntry>().Add(entry); }
        DictionaryEntryReader.Apply(entry, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DictionaryEntrySnapshot>();
        if (before is null) return;
        var entry = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e => e.Id == before.Id);
        if (entry is not null) db.Set<CustomerDictionaryEntry>().Remove(entry);
    }
}
