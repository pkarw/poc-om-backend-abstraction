using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// The custom-field DEFINITION admin surface (upstream api/definitions.{manage,batch,restore}.ts). OM's
/// file-based routing keeps the dot literal in the URL, so these are <c>/api/entities/definitions.manage</c>
/// etc. — ASP.NET treats the dot as a literal path segment char, so the templates match 1:1.
///   • GET  <c>/definitions.manage</c>  — all scoped defs + tombstoned keys for ONE entity (admin editor read)
///   • POST <c>/definitions.batch</c>   — transactional bulk upsert of an entity's defs (admin editor save)
///   • POST <c>/definitions.restore</c> — un-tombstone one soft-deleted def
/// All require <c>entities.definitions.manage</c>. The enterprise optimistic-lock / mutation guard and the
/// definitions cache are unported (documented no-ops); the fieldsets branch of batch is deferred.
/// </summary>
public static class DefinitionsManageRoutes
{
    private static readonly Regex EntityIdRegex = new("^[a-z0-9_]+:[a-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new("^[a-z0-9_]+$", RegexOptions.Compiled);
    private const int MaxDefinitionsPerBatch = 1000;

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/definitions.manage", (Func<HttpContext, Task<IResult>>)GetManageAsync);
        routes.MapPost("/api/entities/definitions.batch", (Func<HttpContext, Task<IResult>>)PostBatchAsync);
        routes.MapPost("/api/entities/definitions.restore", (Func<HttpContext, Task<IResult>>)PostRestoreAsync);
    }

    // ---- GET /definitions.manage --------------------------------------------------------------
    private static async Task<IResult> GetManageAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;
        if (ctx!.TenantId is null || (ctx.OrganizationId is null && !ctx.IsSuperAdmin))
            return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var entityId = http.Request.Query["entityId"].ToString();
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { error = "entityId is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var rows = await db.Set<CustomFieldDef>().AsNoTracking()
            .Where(d => d.EntityId == entityId
                        && (d.OrganizationId == null || d.OrganizationId == ctx.OrganizationId)
                        && (d.TenantId == null || d.TenantId == ctx.TenantId))
            .ToListAsync();

        var tombstonedKeys = rows.Where(d => d.DeletedAt != null || !d.IsActive).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

        // Dedup active defs by key: highest scope specificity, newest updatedAt as tiebreak.
        var byKey = new Dictionary<string, CustomFieldDef>(StringComparer.Ordinal);
        foreach (var d in rows.Where(d => d.DeletedAt == null && d.IsActive))
        {
            if (!byKey.TryGetValue(d.Key, out var cur)
                || CustomFieldDefsService.ScopeScore(d) > CustomFieldDefsService.ScopeScore(cur)
                || (CustomFieldDefsService.ScopeScore(d) == CustomFieldDefsService.ScopeScore(cur) && d.UpdatedAt > cur.UpdatedAt))
                byKey[d.Key] = d;
        }

        var items = byKey.Values
            .Where(d => !tombstonedKeys.Contains(d.Key))
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new
            {
                id = d.Id.ToString(),
                key = d.Key,
                kind = d.Kind,
                configJson = CustomFieldDefsService.ParseConfig(d.ConfigJson),
                isActive = d.IsActive,
                organizationId = d.OrganizationId?.ToString(),
                tenantId = d.TenantId?.ToString(),
            })
            .ToList();

        // Fieldsets lib is unported — emit the empty defaults the admin editor tolerates.
        return EntitiesHttp.Result(new
        {
            items,
            deletedKeys = tombstonedKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            fieldsets = Array.Empty<object>(),
            settings = new { singleFieldsetPerRecord = true },
        }, 200);
    }

    // ---- POST /definitions.batch --------------------------------------------------------------
    private static async Task<IResult> PostBatchAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;
        if (ctx!.TenantId is null) return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId")?.Trim();
        if (string.IsNullOrEmpty(entityId) || !EntityIdRegex.IsMatch(entityId))
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { entityId = "Enter the entity id in the format: module_name:entity_id" } }, 400);

        if (!body.TryGetProperty("definitions", out var defsEl) || defsEl.ValueKind != JsonValueKind.Array)
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { definitions = "definitions must be an array" } }, 400);
        var defsArr = defsEl.EnumerateArray().ToList();
        if (defsArr.Count > MaxDefinitionsPerBatch)
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { definitions = $"at most {MaxDefinitionsPerBatch} definitions" } }, 400);
        if (defsArr.Count == 0) return EntitiesHttp.Result(new { ok = true }, 200); // empty batch is valid

        // Validate keys/kinds up front.
        for (var i = 0; i < defsArr.Count; i++)
        {
            var key = EntitiesHttp.StringProp(defsArr[i], "key")?.Trim();
            var kind = EntitiesHttp.StringProp(defsArr[i], "kind")?.Trim();
            if (string.IsNullOrEmpty(key) || !KeyRegex.IsMatch(key) || string.IsNullOrEmpty(kind) || !CustomFieldKinds.All.Contains(kind))
                return EntitiesHttp.Result(new { error = "Validation failed", details = new { index = i, message = "invalid key or kind" } }, 400);
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var keys = defsArr.Select(d => EntitiesHttp.StringProp(d, "key")!.Trim()).ToList();
        var existing = await db.Set<CustomFieldDef>()
            .Where(d => d.EntityId == entityId && keys.Contains(d.Key)
                        && d.OrganizationId == ctx.OrganizationId && d.TenantId == ctx.TenantId)
            .ToListAsync();
        var existingByKey = existing.ToDictionary(d => d.Key, StringComparer.Ordinal);

        // Explicit transaction on a relational store so a mid-batch failure rolls back the whole set;
        // the in-memory provider doesn't support transactions (SaveChanges is atomic there anyway).
        var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            for (var idx = 0; idx < defsArr.Count; idx++)
            {
                var el = defsArr[idx];
                var key = EntitiesHttp.StringProp(el, "key")!.Trim();
                var kind = EntitiesHttp.StringProp(el, "kind")!.Trim();
                if (!existingByKey.TryGetValue(key, out var def))
                {
                    def = new CustomFieldDef { Id = Guid.NewGuid(), EntityId = entityId, Key = key, OrganizationId = ctx.OrganizationId, TenantId = ctx.TenantId, CreatedAt = now };
                    db.Set<CustomFieldDef>().Add(def);
                    existingByKey[key] = def;
                }
                def.Kind = kind;

                var cfg = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                if (el.TryGetProperty("configJson", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object)
                    foreach (var p in cfgEl.EnumerateObject()) cfg[p.Name] = JsonValues.ToClr(p.Value);
                if (cfg.GetValueOrDefault("label") is not string lbl || string.IsNullOrWhiteSpace(lbl)) cfg["label"] = key;
                if (!cfg.ContainsKey("formEditable")) cfg["formEditable"] = true;
                if (!cfg.ContainsKey("listVisible")) cfg["listVisible"] = true;
                if (kind == "multiline" && !cfg.ContainsKey("editor")) cfg["editor"] = "markdown";
                cfg["priority"] = idx; // persist array order
                def.ConfigJson = JsonSerializer.Serialize(cfg);
                def.IsActive = !el.TryGetProperty("isActive", out var act) || act.ValueKind != JsonValueKind.False;
                def.UpdatedAt = now;
                if (def.IsActive) def.DeletedAt = null;
            }
            await db.SaveChangesAsync();
            if (tx is not null) await tx.CommitAsync();
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync();
            return EntitiesHttp.Result(new { error = "Failed to save definitions batch" }, 500);
        }
        finally { if (tx is not null) await tx.DisposeAsync(); }
        // PARITY: fieldsets/singleFieldsetPerRecord branch + definitions cache invalidation are deferred.
        return EntitiesHttp.Result(new { ok = true }, 200);
    }

    // ---- POST /definitions.restore ------------------------------------------------------------
    private static async Task<IResult> PostRestoreAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId")?.Trim();
        var key = EntitiesHttp.StringProp(body, "key")?.Trim();
        if (string.IsNullOrEmpty(entityId) || string.IsNullOrEmpty(key))
            return EntitiesHttp.Result(new { error = "entityId and key are required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var def = await db.Set<CustomFieldDef>()
            .FirstOrDefaultAsync(d => d.EntityId == entityId && d.Key == key
                                      && d.OrganizationId == ctx!.OrganizationId && d.TenantId == ctx.TenantId);
        if (def is null) return EntitiesHttp.Result(new { error = "Not found" }, 404);

        def.DeletedAt = null;
        def.IsActive = true;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return EntitiesHttp.Result(new { ok = true }, 200);
    }
}
