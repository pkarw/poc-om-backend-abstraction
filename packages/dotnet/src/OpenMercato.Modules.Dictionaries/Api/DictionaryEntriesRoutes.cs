using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Dictionaries.Commands;
using OpenMercato.Modules.Dictionaries.Data;
using OpenMercato.Modules.Dictionaries.Lib;

namespace OpenMercato.Modules.Dictionaries.Api;

/// <summary>
/// <c>/api/dictionaries/{id}/entries…</c> — the port of upstream <c>api/[dictionaryId]/entries</c>
/// (list/create), <c>entries/[entryId]</c> (update/delete), <c>entries/reorder</c>, and
/// <c>entries/set-default</c>. All writes go through the command bus; list honors inheritance and the
/// dictionary's <c>entry_sort_mode</c>.
/// </summary>
public static class DictionaryEntriesRoutes
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dictionaries/{id}/entries", (Func<HttpContext, string, Task<IResult>>)ListAsync);
        routes.MapPost("/api/dictionaries/{id}/entries", (Func<HttpContext, string, Task<IResult>>)CreateAsync);
        routes.MapPatch("/api/dictionaries/{id}/entries/{entryId}", (Func<HttpContext, string, string, Task<IResult>>)PatchAsync);
        routes.MapDelete("/api/dictionaries/{id}/entries/{entryId}", (Func<HttpContext, string, string, Task<IResult>>)DeleteAsync);
        routes.MapPost("/api/dictionaries/{id}/entries/reorder", (Func<HttpContext, string, Task<IResult>>)ReorderAsync);
        routes.MapPost("/api/dictionaries/{id}/entries/set-default", (Func<HttpContext, string, Task<IResult>>)SetDefaultAsync);
    }

    // ---- list ---------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.view");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var readable = await DictionariesHttp.ReadableOrganizationIdsAsync(http, ctx!);
        var dictionary = await DictionariesRoutes.LoadReadableAsync(http, ctx!, dictionaryId, readable);
        if (dictionary is null) return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var entries = await db.Set<DictionaryEntry>().AsNoTracking()
            .Where(e => e.DictionaryId == dictionary.Id && e.OrganizationId == dictionary.OrganizationId && e.TenantId == dictionary.TenantId)
            .ToListAsync();
        var sorted = DictionaryUtils.Sort(entries, DictionaryEntrySortModes.Resolve(dictionary.EntrySortMode));
        return DictionariesHttp.Result(new { items = sorted.Select(ProjectEntry).ToList() }, 200);
    }

    // ---- create -------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        var value = DictionariesHttp.Str(body, "value")?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 150)
            return DictionariesHttp.Result(new { error = "Validation failed", field = "value" }, 400);
        if (!ValidColor(body, out var colorError)) return colorError!;

        var input = new DictionaryEntryCreateInput(
            dictionaryId, value,
            DictionariesHttp.Str(body, "label"),
            DictionariesHttp.Str(body, "color"),
            DictionariesHttp.Str(body, "icon"),
            DictionariesHttp.Int(body, "position"));

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryEntryCreateInput, DictionaryEntryResult>("dictionaries.entries.create", input, ctx!);
            var entry = await http.RequestServices.GetRequiredService<AppDbContext>()
                .Set<DictionaryEntry>().AsNoTracking().FirstAsync(e => e.Id == Guid.Parse(r.Result.EntryId));
            DictionariesHttp.SetOperationHeader(http, r.LogEntry);
            return DictionariesHttp.Result(ProjectEntry(entry), 201);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- update -------------------------------------------------------------------------------

    private static async Task<IResult> PatchAsync(HttpContext http, string id, string entryId)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out _) || !Guid.TryParse(entryId, out var eid))
            return DictionariesHttp.Result(new { error = "Dictionary entry not found" }, 404);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        var provided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "value", "label", "color", "icon", "position", "isDefault" })
            if (DictionariesHttp.Has(body, name)) provided.Add(name);
        if (provided.Count == 0)
            return DictionariesHttp.Result(new { error = "Provide at least one field to update." }, 400);
        if (!ValidColor(body, out var colorError)) return colorError!;

        var input = new DictionaryEntryUpdateInput(
            eid,
            DictionariesHttp.Str(body, "value"),
            DictionariesHttp.Str(body, "label"),
            DictionariesHttp.Str(body, "color"),
            DictionariesHttp.Str(body, "icon"),
            DictionariesHttp.Int(body, "position"),
            DictionariesHttp.Bool(body, "isDefault"),
            provided);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryEntryUpdateInput, DictionaryEntryResult>("dictionaries.entries.update", input, ctx!);
            var entry = await http.RequestServices.GetRequiredService<AppDbContext>()
                .Set<DictionaryEntry>().AsNoTracking().FirstAsync(e => e.Id == Guid.Parse(r.Result.EntryId));
            DictionariesHttp.SetOperationHeader(http, r.LogEntry);
            return DictionariesHttp.Result(ProjectEntry(entry), 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- delete -------------------------------------------------------------------------------

    private static async Task<IResult> DeleteAsync(HttpContext http, string id, string entryId)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(entryId, out var eid))
            return DictionariesHttp.Result(new { error = "Dictionary entry not found" }, 404);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryEntryDeleteInput, DictionaryEntryResult>("dictionaries.entries.delete", new DictionaryEntryDeleteInput(eid), ctx!);
            DictionariesHttp.SetOperationHeader(http, r.LogEntry);
            return DictionariesHttp.Result(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- reorder ------------------------------------------------------------------------------

    private static async Task<IResult> ReorderAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);
        if (ctx!.OrganizationId is not { } orgId || ctx.TenantId is not { } tenantId)
            return DictionariesHttp.Result(new { error = "Organization context is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().AsNoTracking().FirstOrDefaultAsync(d =>
            d.Id == dictionaryId && d.OrganizationId == orgId && d.TenantId == tenantId && d.DeletedAt == null);
        if (dictionary is null) return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        if (!body.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return DictionariesHttp.Result(new { error = "Validation failed", field = "entries" }, 400);
        var positions = new List<ReorderEntryPosition>();
        foreach (var el in arr.EnumerateArray())
        {
            var entryId = DictionariesHttp.Str(el, "id");
            var pos = DictionariesHttp.Int(el, "position");
            if (entryId is null || !Guid.TryParse(entryId, out var g) || pos is null || pos < 0)
                return DictionariesHttp.Result(new { error = "Validation failed", field = "entries" }, 400);
            positions.Add(new ReorderEntryPosition(g, pos.Value));
        }

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<ReorderDictionaryEntriesInput, ReorderDictionaryEntriesResult>(
                "dictionaries.entries.reorder",
                new ReorderDictionaryEntriesInput(dictionaryId, tenantId, orgId, positions), ctx!);
            DictionariesHttp.SetOperationHeader(http, r.LogEntry);
            return DictionariesHttp.Result(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- set-default --------------------------------------------------------------------------

    private static async Task<IResult> SetDefaultAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);
        if (ctx!.OrganizationId is not { } orgId || ctx.TenantId is not { } tenantId)
            return DictionariesHttp.Result(new { error = "Organization context is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().AsNoTracking().FirstOrDefaultAsync(d =>
            d.Id == dictionaryId && d.OrganizationId == orgId && d.TenantId == tenantId && d.DeletedAt == null);
        if (dictionary is null) return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        var entryId = DictionariesHttp.Str(body, "entryId");
        if (entryId is null || !Guid.TryParse(entryId, out var eid))
            return DictionariesHttp.Result(new { error = "Validation failed", field = "entryId" }, 400);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<SetDefaultDictionaryEntryInput, SetDefaultDictionaryEntryResult>(
                "dictionaries.entries.set_default",
                new SetDefaultDictionaryEntryInput(dictionaryId, tenantId, orgId, eid), ctx!);
            DictionariesHttp.SetOperationHeader(http, r.LogEntry);
            return DictionariesHttp.Result(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static bool ValidColor(JsonElement body, out IResult? error)
    {
        error = null;
        if (!DictionariesHttp.Has(body, "color")) return true;
        if (body.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String)
        {
            var raw = c.GetString();
            if (!string.IsNullOrEmpty(raw) && System.Text.RegularExpressions.Regex.IsMatch(raw.Trim(), "^#([0-9a-fA-F]{6})$"))
                return true;
            if (!string.IsNullOrEmpty(raw))
            {
                error = DictionariesHttp.Result(new { error = "Color must be a valid six-digit hex code like #3366ff" }, 400);
                return false;
            }
        }
        return true; // null clears
    }

    private static object ProjectEntry(DictionaryEntry e) => new Dictionary<string, object?>
    {
        ["id"] = e.Id.ToString(),
        ["value"] = e.Value,
        ["label"] = e.Label,
        ["color"] = e.Color,
        ["icon"] = e.Icon,
        ["position"] = e.Position,
        ["isDefault"] = e.IsDefault,
        ["createdAt"] = e.CreatedAt,
        ["updatedAt"] = e.UpdatedAt,
    };
}
