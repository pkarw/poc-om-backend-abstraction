using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Dictionaries.Data;
using OpenMercato.Modules.Dictionaries.Lib;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Module dictionaries — the port of upstream <c>api/dictionaries/*</c>. Hand-written command-bus
/// routes over the module-owned <c>customer_dictionary_entries</c> table (NOT the generic dictionaries
/// module), plus the <c>currency</c> route that reads the generic <c>Dictionary</c>/<c>DictionaryEntry</c>
/// and the per-kind UI <c>kind-settings</c>. Envelopes/status codes are byte-faithful: list returns
/// <c>{sortMode, items}</c> (org inheritance dedup, sorted by settings), create returns 201 when the
/// upsert mode is <c>created</c> else 200, delete returns <c>{success:true}</c>, and the
/// <c>person_company_role</c> in-use guard surfaces 409 <c>role_type_in_use</c>.
/// </summary>
public sealed class DictionariesRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.people.view" };
    private static readonly string[] Manage = { "customers.settings.manage" };
    private const string ListFail = "Failed to load dictionary entries";
    private const string SaveFail = "Failed to save dictionary entry";
    private const string DeleteFail = "Failed to delete dictionary entry";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/dictionaries/currency", (Func<HttpContext, Task<IResult>>)CurrencyAsync);
        routes.MapGet("/api/customers/dictionaries/kind-settings", (Func<HttpContext, Task<IResult>>)KindSettingsListAsync);
        routes.MapPatch("/api/customers/dictionaries/kind-settings", (Func<HttpContext, Task<IResult>>)KindSettingsUpsertAsync);
        routes.MapGet("/api/customers/dictionaries/{kind}", (Func<HttpContext, string, Task<IResult>>)ListAsync);
        routes.MapPost("/api/customers/dictionaries/{kind}", (Func<HttpContext, string, Task<IResult>>)CreateAsync);
        routes.MapPatch("/api/customers/dictionaries/{kind}/{id}", (Func<HttpContext, string, string, Task<IResult>>)UpdateAsync);
        routes.MapDelete("/api/customers/dictionaries/{kind}/{id}", (Func<HttpContext, string, string, Task<IResult>>)DeleteAsync);
    }

    private sealed class Item
    {
        public Guid Id; public string Value = ""; public string Label = ""; public string? Color; public string? Icon;
        public Guid OrganizationId; public bool IsInherited; public DateTimeOffset CreatedAt; public DateTimeOffset UpdatedAt;
        public int? UsageCount;
    }

    // ---- GET /dictionaries/{kind} -----------------------------------------------------------------
    private static async Task<IResult> ListAsync(HttpContext http, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var tenant = ctx!.TenantId ?? Guid.Empty;
            var selected = ResolveSelectedOrg(http, ctx);
            var (routeKind, mappedKind) = DictionaryContext.MapKind(kind);
            if (selected is null) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
            var org = selected.Value;

            var settings = await DictionaryContext.LoadSettingsAsync(db, tenant, org);
            var sortModes = DictionaryContext.ParseSortModes(settings?.DictionarySortModes);
            var sortMode = DictionaryEntrySortModes.Resolve(sortModes.TryGetValue(routeKind, out var m) ? m : null);

            var readable = await DictionaryContext.ReadableOrganizationIdsAsync(db, tenant, ctx);
            if (!readable.Contains(org)) readable.Insert(0, org);
            var scoped = readable.Count > 0 ? readable : new List<Guid> { org };

            var rows = await db.Set<CustomerDictionaryEntry>().AsNoTracking()
                .Where(e => e.TenantId == tenant && e.Kind == mappedKind && scoped.Contains(e.OrganizationId))
                .ToListAsync();

            var priority = scoped.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            static int ByLabel(CustomerDictionaryEntry a, CustomerDictionaryEntry b) =>
                string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);

            var local = rows.Where(e => e.OrganizationId == org).ToList();
            local.Sort(ByLabel);
            var inherited = rows.Where(e => e.OrganizationId != org).ToList();
            inherited.Sort((a, b) =>
            {
                var pa = priority.TryGetValue(a.OrganizationId, out var x) ? x : int.MaxValue;
                var pb = priority.TryGetValue(b.OrganizationId, out var y) ? y : int.MaxValue;
                return pa != pb ? pa - pb : ByLabel(a, b);
            });

            var preferred = new List<CustomerDictionaryEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in local.Concat(inherited))
            {
                var nv = string.IsNullOrWhiteSpace(e.NormalizedValue) ? e.Value.Trim().ToLowerInvariant() : e.NormalizedValue.Trim();
                if (nv.Length == 0 || !seen.Add(nv)) continue;
                preferred.Add(e);
            }

            var items = new List<Item>();
            foreach (var e in preferred)
            {
                int? usage = null;
                if (mappedKind == "person_company_role")
                    usage = (await DictionaryContext.LoadRoleTypeUsageAsync(db, tenant, e.OrganizationId, e.Value)).Total;
                items.Add(new Item
                {
                    Id = e.Id, Value = e.Value, Label = e.Label, Color = e.Color, Icon = e.Icon,
                    OrganizationId = e.OrganizationId, IsInherited = e.OrganizationId != org,
                    CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt, UsageCount = usage,
                });
            }

            var sorted = SortItems(items, sortMode);
            var payload = sorted.Select(i => (object)Project(i, mappedKind == "person_company_role")).ToList();
            return CustomersHttp.Json(new { sortMode, items = payload }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = ListFail }, 400); }
    }

    // ---- POST /dictionaries/{kind} ----------------------------------------------------------------
    private static async Task<IResult> CreateAsync(HttpContext http, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        try
        {
            var org = ctx!.OrganizationId;
            if (org is null || org == Guid.Empty) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
            var (_, mappedKind) = DictionaryContext.MapKind(kind);
            var body = await CustomersHttp.ReadBodyAsync(http);
            if (!ValidateEntryWrite(body, requireValue: true)) throw new FormatException("Invalid payload");

            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<DictionaryEntryCreateInput, DictionaryEntryWriteResult>(
                "customers.dictionaryEntries.create",
                new DictionaryEntryCreateInput(org.Value, ctx.TenantId ?? Guid.Empty, mappedKind, body), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(EntryBody(r.Result.EntryId, r.Result.Value, r.Result.Label, r.Result.Color, r.Result.Icon, r.Result.OrganizationId),
                r.Result.Mode == "created" ? 201 : 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = SaveFail }, 400); }
    }

    // ---- PATCH /dictionaries/{kind}/{id} ----------------------------------------------------------
    private static async Task<IResult> UpdateAsync(HttpContext http, string kind, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        try
        {
            var org = ctx!.OrganizationId;
            if (org is null || org == Guid.Empty) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
            var (_, mappedKind) = DictionaryContext.MapKind(kind);
            if (!Guid.TryParse(id, out var entryId)) throw new FormatException("Invalid id");
            var body = await CustomersHttp.ReadBodyAsync(http);
            if (!HasAnyEntryField(body)) throw new FormatException("No changes provided");
            if (!ValidateEntryWrite(body, requireValue: false)) throw new FormatException("Invalid payload");

            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            CommandExecuteResult<DictionaryEntryUpdateResult> r;
            try
            {
                r = await bus.ExecuteWithLog<DictionaryEntryUpdateInput, DictionaryEntryUpdateResult>(
                    "customers.dictionaryEntries.update",
                    new DictionaryEntryUpdateInput(entryId, org.Value, ctx.TenantId ?? Guid.Empty, mappedKind, body), ctx);
            }
            catch (CommandHttpException ex)
            {
                return CustomersHttp.Json(RewriteEntryError(ex, forDelete: false), ex.Status);
            }
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(EntryBody(r.Result.EntryId, r.Result.Value, r.Result.Label, r.Result.Color, r.Result.Icon, org.Value), 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = SaveFail }, 400); }
    }

    // ---- DELETE /dictionaries/{kind}/{id} ---------------------------------------------------------
    private static async Task<IResult> DeleteAsync(HttpContext http, string kind, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        try
        {
            var org = ctx!.OrganizationId;
            if (org is null || org == Guid.Empty) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
            var (_, mappedKind) = DictionaryContext.MapKind(kind);
            if (!Guid.TryParse(id, out var entryId)) throw new FormatException("Invalid id");

            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            CommandExecuteResult<DictionaryEntryDeleteResult> r;
            try
            {
                r = await bus.ExecuteWithLog<DictionaryEntryDeleteInput, DictionaryEntryDeleteResult>(
                    "customers.dictionaryEntries.delete",
                    new DictionaryEntryDeleteInput(entryId, org.Value, ctx.TenantId ?? Guid.Empty, mappedKind), ctx);
            }
            catch (CommandHttpException ex)
            {
                return CustomersHttp.Json(RewriteEntryError(ex, forDelete: true), ex.Status);
            }
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { success = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = DeleteFail }, 400); }
    }

    // ---- GET /dictionaries/currency ---------------------------------------------------------------
    private static async Task<IResult> CurrencyAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var tenant = ctx!.TenantId ?? Guid.Empty;
            var selected = ctx.OrganizationId;
            var readable = await DictionaryContext.ReadableOrganizationIdsAsync(db, tenant, ctx);

            var q = db.Set<Dictionary>().AsNoTracking()
                .Where(d => d.TenantId == tenant && (d.Key == "currency" || d.Key == "currencies") && d.DeletedAt == null && d.IsActive);
            if (readable.Count > 0) q = q.Where(d => readable.Contains(d.OrganizationId));
            var dictionaries = await q.OrderBy(d => d.OrganizationId).ThenBy(d => d.CreatedAt).ToListAsync();

            var dictionary = dictionaries.FirstOrDefault(d => d.OrganizationId == selected) ?? dictionaries.FirstOrDefault();
            if (dictionary is null)
                return CustomersHttp.Json(new { error = "Currency dictionary is not configured yet." }, 404);

            var entries = await db.Set<DictionaryEntry>().AsNoTracking()
                .Where(e => e.DictionaryId == dictionary.Id && e.TenantId == tenant && e.OrganizationId == dictionary.OrganizationId)
                .OrderBy(e => e.Label).ToListAsync();

            return CustomersHttp.Json(new
            {
                id = dictionary.Id.ToString(),
                entries = entries.Select(e => new { id = e.Id.ToString(), value = e.Value, label = e.Label }).ToList(),
            }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = "Failed to load currency dictionary." }, 500); }
    }

    // ---- GET /dictionaries/kind-settings ----------------------------------------------------------
    private static async Task<IResult> KindSettingsListAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var tenant = ctx!.TenantId ?? Guid.Empty;
            var org = ResolveSelectedOrg(http, ctx);
            var query = db.Set<CustomerDictionaryKindSetting>().AsNoTracking().Where(s => s.TenantId == tenant);
            if (org is { } o) query = query.Where(s => s.OrganizationId == o);
            var rows = await query.OrderBy(s => s.SortOrder).ThenBy(s => s.Kind).ToListAsync();
            return CustomersHttp.Json(new
            {
                items = rows.Select(s => new { id = s.Id.ToString(), kind = s.Kind, selectionMode = s.SelectionMode, visibleInTags = s.VisibleInTags, sortOrder = s.SortOrder }).ToList(),
            }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = "Failed to load kind settings" }, 500); }
    }

    // ---- PATCH /dictionaries/kind-settings --------------------------------------------------------
    private static async Task<IResult> KindSettingsUpsertAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        try
        {
            var org = ctx!.OrganizationId;
            if (org is null || org == Guid.Empty) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
            var body = await CustomersHttp.ReadBodyAsync(http);
            var kind = CustomersHttp.Str(body, "kind")?.Trim();
            if (string.IsNullOrEmpty(kind) || kind.Length > 100) throw new FormatException("Invalid kind");
            var selectionMode = CustomersHttp.Str(body, "selectionMode");
            if (selectionMode is not null && selectionMode != "single" && selectionMode != "multi") throw new FormatException("Invalid selectionMode");
            var visibleInTags = CustomersHttp.Bool(body, "visibleInTags");
            var sortOrder = CustomersHttp.Int(body, "sortOrder");
            if (sortOrder is < 0) throw new FormatException("Invalid sortOrder");

            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<KindSettingsUpsertInput, KindSettingsUpsertResult>(
                "customers.dictionaryKindSettings.upsert",
                new KindSettingsUpsertInput(org.Value, ctx.TenantId ?? Guid.Empty, kind, selectionMode, visibleInTags, sortOrder), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new
            {
                id = r.Result.SettingId, kind = r.Result.Kind, selectionMode = r.Result.SelectionMode,
                visibleInTags = r.Result.VisibleInTags, sortOrder = r.Result.SortOrder,
            }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = "Failed to update kind setting" }, 500); }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static Guid? ResolveSelectedOrg(HttpContext http, CommandContext ctx)
    {
        var q = http.Request.Query["organizationId"].ToString();
        if (!string.IsNullOrEmpty(q) && Guid.TryParse(q, out var g)) return g;
        return ctx.OrganizationId is { } o && o != Guid.Empty ? o : null;
    }

    private static object EntryBody(string id, string value, string label, string? color, string? icon, Guid org) => new
    {
        id, value, label, color, icon, organizationId = org.ToString(), isInherited = false,
    };

    private static object Project(Item i, bool includeUsage) => includeUsage
        ? new
        {
            id = i.Id.ToString(), value = i.Value, label = i.Label, color = i.Color, icon = i.Icon,
            organizationId = i.OrganizationId.ToString(), isInherited = i.IsInherited,
            createdAt = CustomersHttp.Iso(i.CreatedAt), updatedAt = CustomersHttp.Iso(i.UpdatedAt), usageCount = i.UsageCount ?? 0,
        }
        : new
        {
            id = i.Id.ToString(), value = i.Value, label = i.Label, color = i.Color, icon = i.Icon,
            organizationId = i.OrganizationId.ToString(), isInherited = i.IsInherited,
            createdAt = CustomersHttp.Iso(i.CreatedAt), updatedAt = CustomersHttp.Iso(i.UpdatedAt),
        };

    private static List<Item> SortItems(List<Item> items, string mode)
    {
        var list = items.ToList();
        list.Sort((a, b) =>
        {
            var primary = mode switch
            {
                DictionaryEntrySortModes.LabelDesc => Cmp(Coalesce(b.Label, b.Value), Coalesce(a.Label, a.Value)),
                DictionaryEntrySortModes.ValueAsc => Cmp(a.Value, b.Value),
                DictionaryEntrySortModes.ValueDesc => Cmp(b.Value, a.Value),
                DictionaryEntrySortModes.CreatedAtAsc => a.CreatedAt.CompareTo(b.CreatedAt),
                DictionaryEntrySortModes.CreatedAtDesc => b.CreatedAt.CompareTo(a.CreatedAt),
                _ => Cmp(Coalesce(a.Label, a.Value), Coalesce(b.Label, b.Value)),
            };
            return primary != 0 ? primary : Cmp(a.Id.ToString(), b.Id.ToString());
        });
        return list;
    }

    private static string Coalesce(string? a, string? b) => !string.IsNullOrEmpty(a) ? a! : (b ?? string.Empty);
    private static int Cmp(string? a, string? b) => string.Compare(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool HasAnyEntryField(System.Text.Json.JsonElement body) =>
        CustomersHttp.Has(body, "value") || CustomersHttp.Has(body, "label") || CustomersHttp.Has(body, "color") || CustomersHttp.Has(body, "icon");

    private static bool ValidateEntryWrite(System.Text.Json.JsonElement body, bool requireValue)
    {
        var hasValue = CustomersHttp.Has(body, "value");
        if (requireValue && !hasValue) return false;
        if (hasValue)
        {
            var v = CustomersHttp.Str(body, "value")?.Trim();
            if (string.IsNullOrEmpty(v) || v.Length > 150) return false;
        }
        if (CustomersHttp.Has(body, "label"))
        {
            var l = CustomersHttp.Str(body, "label");
            if (l is not null && l.Trim().Length > 150) return false;
        }
        if (CustomersHttp.Has(body, "color"))
        {
            var c = body.GetProperty("color");
            if (c.ValueKind == System.Text.Json.JsonValueKind.String && DictionaryContext.NormalizeColor(c.GetString()) is null && !string.IsNullOrWhiteSpace(c.GetString()))
                return false;
        }
        if (CustomersHttp.Has(body, "icon"))
        {
            var ic = body.GetProperty("icon");
            if (ic.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = ic.GetString()?.Trim() ?? string.Empty;
                if (s.Length is 0 or > 48) return false;
            }
        }
        return true;
    }

    private static object RewriteEntryError(CommandHttpException ex, bool forDelete)
    {
        if (ex.Status == 409 && ex.Body is IDictionary<string, object?> d && (d.TryGetValue("code", out var code) && (code as string) == "role_type_in_use"))
        {
            var count = d.TryGetValue("usageCount", out var c) && c is int n ? n : 0;
            var message = forDelete
                ? $"This role type is assigned to {count} records. Remove or replace those assignments before deleting it."
                : $"This role type is assigned to {count} records. Remove or replace those assignments before changing its value.";
            var body = new Dictionary<string, object?>(d) { ["error"] = message };
            return body;
        }
        return ex.Body;
    }
}
