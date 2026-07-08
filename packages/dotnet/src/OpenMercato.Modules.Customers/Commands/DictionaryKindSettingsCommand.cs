using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// <c>customers.dictionaryKindSettings.upsert</c> — the port of upstream
/// <c>commands/dictionaryKindSettings.ts</c>. Upserts a per-(tenant,org,kind) UI setting (defaults
/// selectionMode 'single', visibleInTags true, sortOrder 0). Undoable via before/after snapshots.
///
/// NOTE — upstream also emits CRUD side-effects (events <c>customers</c>/<c>dictionary_kind_setting</c>,
/// persistent; indexer <c>customers:customer_dictionary_kind_setting</c>). That projection is deferred:
/// there is no query-index entity type for kind settings yet. See ADR — kind-setting CRUD side-effects.
/// </summary>
public sealed class UpsertDictionaryKindSettingCommand
    : ICommand<KindSettingsUpsertInput, KindSettingsUpsertResult>,
      ICommandLogMetadataBuilder<KindSettingsUpsertInput, KindSettingsUpsertResult>, IUndoableCommand
{
    public string CommandId => "customers.dictionaryKindSettings.upsert";
    private KindSettingSnapshot? _before, _after;
    private bool _created;

    public async Task<KindSettingsUpsertResult> ExecuteAsync(KindSettingsUpsertInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var setting = await db.Set<CustomerDictionaryKindSetting>().FirstOrDefaultAsync(s =>
            s.TenantId == input.TenantId && s.OrganizationId == input.OrganizationId && s.Kind == input.Kind);

        _created = setting is null;
        var now = DateTimeOffset.UtcNow;
        if (setting is null)
        {
            setting = new CustomerDictionaryKindSetting
            {
                Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId, Kind = input.Kind,
                SelectionMode = input.SelectionMode ?? "single", VisibleInTags = input.VisibleInTags ?? true,
                SortOrder = input.SortOrder ?? 0, CreatedAt = now, UpdatedAt = now,
            };
            db.Set<CustomerDictionaryKindSetting>().Add(setting);
        }
        else
        {
            _before = Snapshot(setting);
            if (input.SelectionMode is not null) setting.SelectionMode = input.SelectionMode;
            if (input.VisibleInTags is { } v) setting.VisibleInTags = v;
            if (input.SortOrder is { } o) setting.SortOrder = o;
            setting.UpdatedAt = now;
        }

        _after = Snapshot(setting);
        return new KindSettingsUpsertResult(setting.Id.ToString(), _created, setting.Kind, setting.SelectionMode, setting.VisibleInTags, setting.SortOrder);
    }

    public CommandLogMetadata BuildLog(KindSettingsUpsertInput input, KindSettingsUpsertResult result, CommandContext ctx) => new()
    {
        ActionLabel = _created ? "Create dictionary kind setting" : "Update dictionary kind setting",
        ResourceKind = "customers.dictionaryKindSetting", ResourceId = result.SettingId,
        TenantId = _after?.TenantId, OrganizationId = _after?.OrganizationId,
        SnapshotBefore = _before, SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<KindSettingSnapshot>();
        var before = log.GetSnapshotBefore<KindSettingSnapshot>();
        if (after is null) return;
        var setting = await db.Set<CustomerDictionaryKindSetting>().FirstOrDefaultAsync(s => s.Id == after.Id);
        if (setting is null) return;
        if (before is null) { db.Set<CustomerDictionaryKindSetting>().Remove(setting); return; }
        setting.SelectionMode = before.SelectionMode; setting.VisibleInTags = before.VisibleInTags; setting.SortOrder = before.SortOrder;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<KindSettingSnapshot>();
        if (after is null) return;
        var setting = await db.Set<CustomerDictionaryKindSetting>().FirstOrDefaultAsync(s => s.Id == after.Id);
        if (setting is null)
        {
            setting = new CustomerDictionaryKindSetting { Id = after.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Set<CustomerDictionaryKindSetting>().Add(setting);
        }
        setting.OrganizationId = after.OrganizationId; setting.TenantId = after.TenantId; setting.Kind = after.Kind;
        setting.SelectionMode = after.SelectionMode; setting.VisibleInTags = after.VisibleInTags; setting.SortOrder = after.SortOrder;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static KindSettingSnapshot Snapshot(CustomerDictionaryKindSetting s) =>
        new(s.Id, s.OrganizationId, s.TenantId, s.Kind, s.SelectionMode, s.VisibleInTags, s.SortOrder);
}
