using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>/api/entities/definitions</c> — the custom-field DEFINITION management surface (upstream
/// api/definitions.ts). GET lists active normalized definitions for the requested entity ids (auth
/// required); POST upserts one definition and DELETE soft-deactivates one (both require
/// <c>entities.definitions.manage</c>). Tenant-scoped: global (null) or exact-tenant defs are visible.
/// </summary>
public static class DefinitionsRoutes
{
    private static readonly Regex EntityIdRegex = new("^[a-z0-9_]+:[a-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new("^[a-z0-9_]+$", RegexOptions.Compiled);

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/definitions", (Func<HttpContext, Task<IResult>>)GetAsync);
        routes.MapPost("/api/entities/definitions", (Func<HttpContext, Task<IResult>>)PostAsync);
        routes.MapDelete("/api/entities/definitions", (Func<HttpContext, Task<IResult>>)DeleteAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http);
        if (denied is not null) return denied;

        var entityIds = ParseEntityIds(http.Request);
        if (entityIds.Count == 0) return EntitiesHttp.Result(new { error = "entityId is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var tenantId = ctx!.TenantId;

        var defs = await db.Set<CustomFieldDef>()
            .Where(d => entityIds.Contains(d.EntityId) && d.IsActive && d.DeletedAt == null)
            .Where(d => d.TenantId == null || d.TenantId == tenantId)
            .ToListAsync();

        var items = new List<object>();
        foreach (var entityId in entityIds)
        {
            var byKey = new Dictionary<string, CustomFieldDef>(StringComparer.Ordinal);
            foreach (var d in defs.Where(x => x.EntityId == entityId))
            {
                if (!byKey.TryGetValue(d.Key, out var existing)) { byKey[d.Key] = d; continue; }
                var ns = CustomFieldDefsService.ScopeScore(d);
                var es = CustomFieldDefsService.ScopeScore(existing);
                if (ns > es || (ns == es && d.UpdatedAt >= existing.UpdatedAt)) byKey[d.Key] = d;
            }
            foreach (var d in byKey.Values.OrderBy(x => Priority(x)))
                items.Add(Normalize(d, entityId));
        }

        return EntitiesHttp.Result(new { items }, 200);
    }

    private static async Task<IResult> PostAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId");
        var key = EntitiesHttp.StringProp(body, "key");
        var kind = EntitiesHttp.StringProp(body, "kind");
        if (entityId is null || !EntityIdRegex.IsMatch(entityId)) return EntitiesHttp.Result(new { error = "Validation failed", field = "entityId" }, 400);
        if (key is null || !KeyRegex.IsMatch(key)) return EntitiesHttp.Result(new { error = "Validation failed", field = "key" }, 400);
        if (kind is null || !CustomFieldKinds.IsKind(kind)) return EntitiesHttp.Result(new { error = "Validation failed", field = "kind" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var def = await db.Set<CustomFieldDef>().FirstOrDefaultAsync(d =>
            d.EntityId == entityId && d.Key == key && d.OrganizationId == ctx!.OrganizationId && d.TenantId == ctx.TenantId);
        var isNew = def is null;
        if (def is null)
        {
            def = new CustomFieldDef { Id = Guid.NewGuid(), EntityId = entityId, Key = key, OrganizationId = ctx!.OrganizationId, TenantId = ctx.TenantId, CreatedAt = now };
            db.Set<CustomFieldDef>().Add(def);
        }
        def.Kind = kind;

        // configJson passthrough (label defaults to key; formEditable/listVisible default true).
        var cfg = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (body.TryGetProperty("configJson", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object)
            foreach (var p in cfgEl.EnumerateObject()) cfg[p.Name] = JsonValues.ToClr(p.Value);
        if (cfg.GetValueOrDefault("label") is not string lbl || string.IsNullOrWhiteSpace(lbl)) cfg["label"] = key;
        if (!cfg.ContainsKey("formEditable")) cfg["formEditable"] = true;
        if (!cfg.ContainsKey("listVisible")) cfg["listVisible"] = true;
        def.ConfigJson = JsonSerializer.Serialize(cfg);
        def.IsActive = !body.TryGetProperty("isActive", out var act) || act.ValueKind != JsonValueKind.False;
        def.UpdatedAt = now;
        if (def.IsActive) def.DeletedAt = null;
        await db.SaveChangesAsync();

        return EntitiesHttp.Result(new { ok = true, item = new { id = def.Id, key = def.Key, kind = def.Kind, configJson = def.ConfigJson, isActive = def.IsActive } }, isNew ? 200 : 200);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId");
        var key = EntitiesHttp.StringProp(body, "key");
        if (entityId is null || key is null) return EntitiesHttp.Result(new { error = "entityId and key are required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var def = await db.Set<CustomFieldDef>().FirstOrDefaultAsync(d =>
            d.EntityId == entityId && d.Key == key && d.OrganizationId == ctx!.OrganizationId && d.TenantId == ctx.TenantId);
        if (def is null) return EntitiesHttp.Result(new { error = "Not found" }, 404);
        var now = DateTimeOffset.UtcNow;
        def.IsActive = false;
        def.UpdatedAt = now;
        def.DeletedAt ??= now;
        await db.SaveChangesAsync();
        return EntitiesHttp.Result(new { ok = true }, 200);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static List<string> ParseEntityIds(HttpRequest req)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in req.Query["entityId"])
            if (!string.IsNullOrWhiteSpace(v) && seen.Add(v!)) ids.Add(v!);
        var combined = req.Query["entityIds"].ToString();
        if (!string.IsNullOrEmpty(combined))
            foreach (var part in combined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(part)) ids.Add(part);
        return ids;
    }

    private static int Priority(CustomFieldDef d)
    {
        var cfg = CustomFieldDefsService.ParseConfig(d.ConfigJson);
        if (cfg is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("priority", out var p) && p.TryGetInt32(out var v)) return v;
        return 0;
    }

    private static object Normalize(CustomFieldDef d, string entityId)
    {
        var cfg = CustomFieldDefsService.ParseConfig(d.ConfigJson);
        string? Str(string n) => cfg is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(string n, bool dflt) => cfg is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True : dflt;
        object? Options() => cfg is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("options", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(JsonValues.ToClr).ToList() : null;

        return new
        {
            key = d.Key,
            kind = d.Kind,
            label = Str("label") ?? d.Key,
            description = Str("description"),
            multi = Bool("multi", false),
            options = Options(),
            optionsUrl = d.Kind == "currency" ? CustomFieldKinds.CurrencyOptionsUrl : Str("optionsUrl"),
            filterable = Bool("filterable", false),
            formEditable = Bool("formEditable", true),
            listVisible = Bool("listVisible", true),
            priority = Priority(d),
            entityId,
        };
    }
}
