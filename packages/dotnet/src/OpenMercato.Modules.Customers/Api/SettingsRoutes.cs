using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Customer settings — the port of upstream <c>api/settings/*</c>. Three per-org settings facets, each
/// a hand-written command-bus route resolving its own context (401 <c>Unauthorized</c>, 400
/// <c>Organization context is required</c>): <c>address-format</c> (GET/PUT, <c>customers.settings.manage</c>),
/// <c>dictionary-sort-modes</c> (GET/PATCH, merges into stored map, <c>customers.settings.manage</c>) and
/// <c>stuck-threshold</c> (GET/PUT, note the different <c>customers.deals.manage</c> feature). Defaults
/// mirror upstream: addressFormat <c>line_first</c>, stuckThresholdDays <c>14</c>.
/// </summary>
public sealed class SettingsRoutes : ICustomersRouteGroup
{
    private static readonly string[] SettingsManage = { "customers.settings.manage" };
    private static readonly string[] DealsManage = { "customers.deals.manage" };
    private const string LookupFail = "Failed to load settings";
    private const string SaveFail = "Failed to save settings";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/settings/address-format", (Func<HttpContext, Task<IResult>>)AddressFormatGetAsync);
        routes.MapPut("/api/customers/settings/address-format", (Func<HttpContext, Task<IResult>>)AddressFormatPutAsync);
        routes.MapGet("/api/customers/settings/dictionary-sort-modes", (Func<HttpContext, Task<IResult>>)SortModesGetAsync);
        routes.MapPatch("/api/customers/settings/dictionary-sort-modes", (Func<HttpContext, Task<IResult>>)SortModesPatchAsync);
        routes.MapGet("/api/customers/settings/stuck-threshold", (Func<HttpContext, Task<IResult>>)StuckThresholdGetAsync);
        routes.MapPut("/api/customers/settings/stuck-threshold", (Func<HttpContext, Task<IResult>>)StuckThresholdPutAsync);
    }

    // ---- address-format ---------------------------------------------------------------------------
    private static async Task<IResult> AddressFormatGetAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, SettingsManage);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var record = await DictionaryContext.LoadSettingsAsync(db, ctx!.TenantId ?? Guid.Empty, org!.Value);
            return CustomersHttp.Json(new { addressFormat = record?.AddressFormat ?? "line_first" }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = LookupFail }, 400); }
    }

    private static async Task<IResult> AddressFormatPutAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, SettingsManage);
        if (denied is not null) return denied;
        try
        {
            var body = await CustomersHttp.ReadBodyAsync(http);
            var addressFormat = CustomersHttp.Str(body, "addressFormat");
            if (addressFormat != "line_first" && addressFormat != "street_first") throw new FormatException("Invalid addressFormat");
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var result = await bus.Execute<SettingsSaveInput, SettingsSaveResult>(
                "customers.settings.save", new SettingsSaveInput(org!.Value, ctx!.TenantId ?? Guid.Empty, addressFormat), ctx);
            return CustomersHttp.Json(new { addressFormat = result.AddressFormat }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = SaveFail }, 400); }
    }

    // ---- dictionary-sort-modes --------------------------------------------------------------------
    private static async Task<IResult> SortModesGetAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, SettingsManage);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var record = await DictionaryContext.LoadSettingsAsync(db, ctx!.TenantId ?? Guid.Empty, org!.Value);
            return CustomersHttp.Json(new { dictionarySortModes = DictionaryContext.ParseSortModes(record?.DictionarySortModes) }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = LookupFail }, 400); }
    }

    private static async Task<IResult> SortModesPatchAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, SettingsManage);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var body = await CustomersHttp.ReadBodyAsync(http);
            if (!CustomersHttp.Has(body, "dictionarySortModes")) throw new FormatException("dictionarySortModes required");
            var incoming = DictionaryContext.ParseSortModes(body.GetProperty("dictionarySortModes").GetRawText());
            var record = await DictionaryContext.LoadSettingsAsync(db, ctx!.TenantId ?? Guid.Empty, org!.Value);
            var merged = DictionaryContext.ParseSortModes(record?.DictionarySortModes);
            foreach (var kv in incoming) merged[kv.Key] = kv.Value;

            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var result = await bus.Execute<DictionarySortModesSaveInput, DictionarySortModesSaveResult>(
                "customers.settings.save_dictionary_sort_modes",
                new DictionarySortModesSaveInput(org!.Value, ctx.TenantId ?? Guid.Empty, merged), ctx);
            // NOTE — dictionary list cache invalidation is a no-op here (no cache strategy in this port).
            return CustomersHttp.Json(new { dictionarySortModes = result.DictionarySortModes }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = SaveFail }, 400); }
    }

    // ---- stuck-threshold --------------------------------------------------------------------------
    private static async Task<IResult> StuckThresholdGetAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, DealsManage);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var record = await DictionaryContext.LoadSettingsAsync(db, ctx!.TenantId ?? Guid.Empty, org!.Value);
            return CustomersHttp.Json(new { stuckThresholdDays = record?.StuckThresholdDays ?? 14 }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = LookupFail }, 400); }
    }

    private static async Task<IResult> StuckThresholdPutAsync(HttpContext http)
    {
        var (ctx, org, denied) = await ResolveAsync(http, DealsManage);
        if (denied is not null) return denied;
        try
        {
            var body = await CustomersHttp.ReadBodyAsync(http);
            var days = CustomersHttp.Int(body, "stuckThresholdDays");
            if (days is null or < 1 or > 365) throw new FormatException("Invalid stuckThresholdDays");
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var result = await bus.Execute<StuckThresholdSaveInput, StuckThresholdSaveResult>(
                "customers.settings.save_stuck_threshold", new StuckThresholdSaveInput(org!.Value, ctx!.TenantId ?? Guid.Empty, days.Value), ctx);
            return CustomersHttp.Json(new { stuckThresholdDays = result.StuckThresholdDays }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = SaveFail }, 400); }
    }

    /// <summary>Resolve auth (401) + require an organization context (400), then check the feature (403).</summary>
    private static async Task<(CommandContext? Ctx, Guid? Org, IResult? Denied)> ResolveAsync(HttpContext http, string[] features)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, features);
        if (denied is not null) return (null, null, denied);
        var org = ctx!.OrganizationId;
        if (org is null || org == Guid.Empty)
            return (null, null, CustomersHttp.Json(new { error = "Organization context is required" }, 400));
        return (ctx, org, null);
    }
}
