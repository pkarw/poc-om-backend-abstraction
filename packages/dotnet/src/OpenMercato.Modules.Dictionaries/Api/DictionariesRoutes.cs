using System.Text.RegularExpressions;
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
/// <c>/api/dictionaries</c> (collection + item) — the port of upstream <c>api/route.ts</c> and
/// <c>api/[dictionaryId]/route.ts</c>. Reads honor org inheritance (self + ancestors) and shape the
/// bespoke <c>{ items }</c> list envelope; writes dispatch through the command bus.
/// </summary>
public static class DictionariesRoutes
{
    private static readonly Regex KeyRegex = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/dictionaries", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapPost("/api/dictionaries", (Func<HttpContext, Task<IResult>>)CreateAsync);
        routes.MapGet("/api/dictionaries/{id}", (Func<HttpContext, string, Task<IResult>>)GetAsync);
        routes.MapPatch("/api/dictionaries/{id}", (Func<HttpContext, string, Task<IResult>>)PatchAsync);
        routes.MapDelete("/api/dictionaries/{id}", (Func<HttpContext, string, Task<IResult>>)DeleteAsync);
    }

    // ---- reads --------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.view");
        if (denied is not null) return denied;

        var includeInactive = DictionariesHttp.ParseBooleanToken(http.Request.Query["includeInactive"]);
        var readable = await DictionariesHttp.ReadableOrganizationIdsAsync(http, ctx!);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();

        var q = db.Set<Dictionary>().AsNoTracking()
            .Where(d => d.TenantId == ctx!.TenantId && d.DeletedAt == null);
        if (readable.Count > 0) q = q.Where(d => readable.Contains(d.OrganizationId));
        if (!includeInactive) q = q.Where(d => d.IsActive);

        var items = (await q.OrderBy(d => d.Name).ToListAsync())
            .Select(d => Project(d, ctx!.OrganizationId, includeManager: true, includeInherited: true))
            .ToList();
        return DictionariesHttp.Result(new { items }, 200);
    }

    private static async Task<IResult> GetAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.view");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var readable = await DictionariesHttp.ReadableOrganizationIdsAsync(http, ctx!);
        var dictionary = await LoadReadableAsync(http, ctx!, dictionaryId, readable);
        if (dictionary is null) return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);
        return DictionariesHttp.Result(Project(dictionary, ctx!.OrganizationId, includeManager: true, includeInherited: true), 200);
    }

    // ---- writes -------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (ctx!.OrganizationId is null)
            return DictionariesHttp.Result(new { error = "Organization context is required" }, 400);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        var key = DictionariesHttp.Str(body, "key")?.Trim();
        var name = DictionariesHttp.Str(body, "name")?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 100 || !KeyRegex.IsMatch(key.ToLowerInvariant()))
            return DictionariesHttp.Result(new { error = "Use lowercase letters, numbers, hyphen, or underscore." }, 400);
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            return DictionariesHttp.Result(new { error = "Validation failed", field = "name" }, 400);

        var input = new DictionaryCreateInput(
            key, name,
            DictionariesHttp.Str(body, "description"),
            DictionariesHttp.Bool(body, "isSystem"),
            DictionariesHttp.Bool(body, "isActive"),
            DictionariesHttp.Str(body, "entrySortMode"));

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryCreateInput, DictionaryResult>("dictionaries.dictionary.create", input, ctx!);
            var created = await http.RequestServices.GetRequiredService<AppDbContext>()
                .Set<Dictionary>().AsNoTracking().FirstAsync(d => d.Id == Guid.Parse(r.Result.Id));
            return DictionariesHttp.Result(ProjectCreated(created), 201);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    private static async Task<IResult> PatchAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        var body = await DictionariesHttp.ReadBodyAsync(http);
        var provided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "key", "name", "description", "isActive", "entrySortMode" })
            if (DictionariesHttp.Has(body, name)) provided.Add(name);
        if (provided.Count == 0)
            return DictionariesHttp.Result(new { error = "Provide at least one field to update." }, 400);

        var input = new DictionaryUpdateInput(
            dictionaryId,
            DictionariesHttp.Str(body, "key"),
            DictionariesHttp.Str(body, "name"),
            DictionariesHttp.Str(body, "description"),
            DictionariesHttp.Bool(body, "isActive"),
            DictionariesHttp.Str(body, "entrySortMode"),
            provided);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryUpdateInput, DictionaryResult>("dictionaries.dictionary.update", input, ctx!);
            var updated = await http.RequestServices.GetRequiredService<AppDbContext>()
                .Set<Dictionary>().AsNoTracking().FirstAsync(d => d.Id == Guid.Parse(r.Result.Id));
            return DictionariesHttp.Result(Project(updated, ctx!.OrganizationId, includeManager: true, includeInherited: false), 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await DictionariesHttp.AuthorizeAsync(http, "dictionaries.manage");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dictionaryId))
            return DictionariesHttp.Result(new { error = "Dictionary not found" }, 404);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            await bus.Execute<DictionaryDeleteInput, DictionaryResult>("dictionaries.dictionary.delete", new DictionaryDeleteInput(dictionaryId), ctx!);
            return DictionariesHttp.Result(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return DictionariesHttp.Result(ex.Body, ex.Status); }
    }

    // ---- helpers ------------------------------------------------------------------------------

    internal static async Task<Dictionary?> LoadReadableAsync(HttpContext http, CommandContext ctx, Guid id, IReadOnlyList<Guid> readable)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = db.Set<Dictionary>().AsNoTracking().Where(d => d.Id == id && d.TenantId == ctx.TenantId && d.DeletedAt == null);
        if (readable.Count > 0) q = q.Where(d => readable.Contains(d.OrganizationId));
        return await q.FirstOrDefaultAsync();
    }

    private static object Project(Dictionary d, Guid? selectedOrg, bool includeManager, bool includeInherited)
    {
        var map = new Dictionary<string, object?>
        {
            ["id"] = d.Id.ToString(),
            ["key"] = d.Key,
            ["name"] = d.Name,
            ["description"] = d.Description,
            ["isSystem"] = d.IsSystem,
            ["isActive"] = d.IsActive,
        };
        if (includeManager) map["managerVisibility"] = d.ManagerVisibility;
        map["entrySortMode"] = DictionaryEntrySortModes.Resolve(d.EntrySortMode);
        map["organizationId"] = d.OrganizationId.ToString();
        if (includeInherited)
            map["isInherited"] = selectedOrg is { } sel && d.OrganizationId != sel;
        map["createdAt"] = d.CreatedAt;
        map["updatedAt"] = d.UpdatedAt;
        return map;
    }

    private static object ProjectCreated(Dictionary d) => new Dictionary<string, object?>
    {
        ["id"] = d.Id.ToString(),
        ["key"] = d.Key,
        ["name"] = d.Name,
        ["description"] = d.Description,
        ["isSystem"] = d.IsSystem,
        ["isActive"] = d.IsActive,
        ["entrySortMode"] = DictionaryEntrySortModes.Resolve(d.EntrySortMode),
        ["createdAt"] = d.CreatedAt,
        ["updatedAt"] = d.UpdatedAt,
    };
}
