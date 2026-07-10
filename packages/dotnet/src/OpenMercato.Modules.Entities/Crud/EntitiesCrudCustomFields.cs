using System.Text.Json;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Entities.Crud;

/// <summary>
/// The real <see cref="ICrudCustomFields"/> — registered by the entities module (last-wins over Core's
/// <c>NoopCrudCustomFields</c>). It is the wire codec the CRUD factory invokes on every read/write:
///   - <see cref="MergeIntoListItemsAsync"/> / <see cref="MergeIntoDetailAsync"/> decorate response
///     records with their EAV values as BARE field names (<c>{ priority: 3 }</c>) plus the canonical
///     <c>customValues</c> map and <c>customFields</c> array (upstream
///     <c>decorateRecordWithCustomFields</c> / <c>normalizeCustomFieldResponse</c>).
///   - <see cref="PersistAsync"/> reads the <c>cf_*</c> / <c>cf:*</c> / <c>customValues</c> /
///     <c>customFields</c> inputs a create/update body carries (upstream <c>splitCustomFieldPayload</c>),
///     validates them against the field defs, and upserts <c>custom_field_values</c> — reproducing the
///     <c>cf_</c> (request) vs bare (response) key convention.
/// </summary>
public sealed class EntitiesCrudCustomFields : ICrudCustomFields
{
    private readonly AppDbContext _db;

    public EntitiesCrudCustomFields(AppDbContext db) => _db = db;

    public async Task MergeIntoListItemsAsync(
        string entityType, IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        var idByItem = items
            .Select(i => (item: i, id: RecordIdOf(i)))
            .Where(x => x.id is not null)
            .ToList();
        var recordIds = idByItem.Select(x => x.id!).Distinct().ToList();
        if (recordIds.Count == 0) return;

        var values = await RecordCustomFields.LoadAsync(_db, entityType, recordIds, ctx.TenantId, ctx.OrganizationId, ct);
        var defs = await CustomFieldDefsService.LoadWinningDefsAsync(_db, entityType, ctx.TenantId, ctx.OrganizationId, ct);
        foreach (var (item, id) in idByItem)
            if (values.TryGetValue(id!, out var map))
                Decorate(item, map, defs);
    }

    public async Task MergeIntoDetailAsync(
        string entityType, IDictionary<string, object?> item, CommandContext ctx, CancellationToken ct = default)
    {
        var id = RecordIdOf(item);
        if (id is null) return;
        var values = await RecordCustomFields.LoadAsync(_db, entityType, new[] { id }, ctx.TenantId, ctx.OrganizationId, ct);
        if (!values.TryGetValue(id, out var map)) { item["customValues"] = null; item["customFields"] = Array.Empty<object>(); return; }
        var defs = await CustomFieldDefsService.LoadWinningDefsAsync(_db, entityType, ctx.TenantId, ctx.OrganizationId, ct);
        Decorate(item, map, defs);
    }

    public async Task PersistAsync(
        string entityType, string recordId, JsonElement body, CommandContext ctx, CancellationToken ct = default)
    {
        var custom = CustomFieldPayload.ExtractCustom(body);
        if (custom.Count == 0) return;

        var defsByKey = await CustomFieldDefsService.LoadWinningDefsAsync(_db, entityType, ctx.TenantId, ctx.OrganizationId, ct);
        var defLikes = defsByKey.Values
            .Select(d => new DefLike(d.Key, d.Kind, CustomFieldDefsService.ParseConfig(d.ConfigJson)))
            .ToList();

        var check = CustomFieldValidation.ValidateValuesAgainstDefs(custom, defLikes, rejectUndeclaredKeys: false);
        if (!check.Ok)
            throw new CommandHttpException(400, new { error = "Validation failed", fields = check.FieldErrors });

        await RecordCustomFields.SetAsync(_db, entityType, recordId, ctx.TenantId, ctx.OrganizationId, custom, ct);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void Decorate(IDictionary<string, object?> item, IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, CustomFieldDef> defs)
    {
        var customValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var customFields = new List<object>();
        foreach (var (key, value) in values)
        {
            // Merge the value under its BARE name so `{ priority: 3 }` is directly on the record, AND under
            // the `cf_<key>` name that upstream DataTables read (frontend mapApiItem collects `cf_*` keys —
            // e.g. the people list "Buying role" column reads `cf_buying_role`). Both are emitted (additive)
            // so bare-key readers keep working.
            item[key] = value;
            item["cf_" + key] = value;
            customValues[key] = value;
            defs.TryGetValue(key, out var def);
            customFields.Add(new
            {
                key,
                label = def is not null ? DefLabel(def, key) : key,
                value,
                kind = def?.Kind,
                multi = def is not null ? CustomFieldDefsService.IsMulti(def) : value is System.Collections.IList,
            });
        }
        item["customValues"] = customValues.Count > 0 ? customValues : null;
        item["customFields"] = customFields;
    }

    private static string DefLabel(CustomFieldDef def, string fallback)
    {
        var cfg = CustomFieldDefsService.ParseConfig(def.ConfigJson);
        if (cfg is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String)
        {
            var s = l.GetString();
            if (!string.IsNullOrEmpty(s)) return s!;
        }
        return fallback;
    }

    private static string? RecordIdOf(IDictionary<string, object?> item)
    {
        if (!item.TryGetValue("id", out var raw) || raw is null) return null;
        return raw switch
        {
            Guid g => g.ToString(),
            string s => s,
            _ => raw.ToString(),
        };
    }
}
