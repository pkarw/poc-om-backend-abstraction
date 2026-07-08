using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>/api/entities/records</c> — CRUD over CUSTOM-entity records (upstream api/records.ts). Records
/// live in the <c>custom_entities_storage</c> jsonb document store; custom-field values are persisted
/// via the shared EAV engine and merged back as bare keys on read. Requires
/// <c>entities.records.view</c> (GET) / <c>entities.records.manage</c> (mutations).
///
/// PARITY-TODO: this is the best-effort doc-storage port. The upstream query-engine projection
/// (cf filters, sort on cf columns, exports, forceCustomEntityStorage), system-vs-custom entity
/// classification/ACL matrix (entityAcl.ts), and command-backed optimistic locking are deferred.
/// </summary>
public static class RecordsRoutes
{
    private static readonly Regex Uuid = new("^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] Reserved = { "id", "created_at", "createdAt", "updated_at", "updatedAt", "deleted_at", "deletedAt" };

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/records", (Func<HttpContext, Task<IResult>>)GetAsync);
        routes.MapPost("/api/entities/records", (Func<HttpContext, Task<IResult>>)PostAsync);
        routes.MapPut("/api/entities/records", (Func<HttpContext, Task<IResult>>)PutAsync);
        routes.MapDelete("/api/entities/records", (Func<HttpContext, Task<IResult>>)DeleteAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.records.view");
        if (denied is not null) return denied;
        var entityId = http.Request.Query["entityId"].ToString();
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { error = "entityId is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var page = Math.Max(int.TryParse(http.Request.Query["page"], out var pg) ? pg : 1, 1);
        var pageSize = Math.Clamp(int.TryParse(http.Request.Query["pageSize"], out var ps) ? ps : 50, 1, 100);

        var q = db.Set<CustomEntityStorage>()
            .Where(s => s.EntityType == entityId && s.DeletedAt == null)
            .Where(s => s.TenantId == null || s.TenantId == ctx!.TenantId);
        if (ctx!.OrganizationId is { } org)
            q = q.Where(s => s.OrganizationId == null || s.OrganizationId == org);

        var total = await q.CountAsync();
        var rows = await q.OrderBy(s => s.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var recordIds = rows.Select(r => r.EntityId).ToList();
        var cfValues = await RecordCustomFields.LoadAsync(db, entityId, recordIds, ctx.TenantId, ctx.OrganizationId);

        var items = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = row.EntityId };
            MergeDoc(item, row.Doc);
            if (cfValues.TryGetValue(row.EntityId, out var map))
                foreach (var (k, v) in map) item[k] = v;
            item["updated_at"] = row.UpdatedAt;
            items.Add(item);
        }

        return EntitiesHttp.Result(new
        {
            items,
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize),
        }, 200);
    }

    private static async Task<IResult> PostAsync(HttpContext http) => await UpsertAsync(http, requireRecordId: false);
    private static async Task<IResult> PutAsync(HttpContext http) => await UpsertAsync(http, requireRecordId: true);

    private static async Task<IResult> UpsertAsync(HttpContext http, bool requireRecordId)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.records.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId");
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { error = "entityId is required" }, 400);
        var recordIdRaw = EntitiesHttp.StringProp(body, "recordId");
        if (requireRecordId && string.IsNullOrEmpty(recordIdRaw)) return EntitiesHttp.Result(new { error = "entityId and recordId are required" }, 400);

        if (ctx!.OrganizationId is not { } targetOrg) return EntitiesHttp.Result(new { error = "Organization context is required" }, 400);

        // Normalize values: strip cf_ prefix + drop reserved/system echo keys.
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (body.TryGetProperty("values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Object)
            foreach (var p in valuesEl.EnumerateObject())
            {
                var k = p.Name.StartsWith("cf_", StringComparison.Ordinal) ? p.Name[3..] : p.Name;
                if (Reserved.Contains(k)) continue;
                values[k] = JsonValues.ToClr(p.Value);
            }

        // Resolve/generate record id (non-uuid/sentinel → create).
        var recordId = NormalizeRecordId(recordIdRaw) ?? Guid.NewGuid().ToString();

        // Validate against declared defs (untrusted generic endpoint → reject undeclared keys).
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var defsByKey = await CustomFieldDefsService.LoadWinningDefsAsync(db, entityId, ctx.TenantId, targetOrg);
        var defLikes = defsByKey.Values.Select(d => new DefLike(d.Key, d.Kind, CustomFieldDefsService.ParseConfig(d.ConfigJson))).ToList();
        var check = CustomFieldValidation.ValidateValuesAgainstDefs(values, defLikes, rejectUndeclaredKeys: true);
        if (!check.Ok) return EntitiesHttp.Result(new { error = "Validation failed", fields = check.FieldErrors }, 400);

        var now = DateTimeOffset.UtcNow;
        var storage = await db.Set<CustomEntityStorage>().FirstOrDefaultAsync(s =>
            s.EntityType == entityId && s.EntityId == recordId && s.OrganizationId == targetOrg);
        if (storage is null)
        {
            storage = new CustomEntityStorage { Id = Guid.NewGuid(), EntityType = entityId, EntityId = recordId, OrganizationId = targetOrg, TenantId = ctx.TenantId, CreatedAt = now };
            db.Set<CustomEntityStorage>().Add(storage);
        }
        storage.Doc = JsonSerializer.Serialize(values, EntitiesHttp.Json);
        storage.UpdatedAt = now;
        storage.DeletedAt = null;
        await db.SaveChangesAsync();

        await RecordCustomFields.SetAsync(db, entityId, recordId, ctx.TenantId, targetOrg, values);

        return EntitiesHttp.Result(new { ok = true, item = new { entityId, recordId } }, 200);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.records.manage");
        if (denied is not null) return denied;

        var body = http.Request.Query.ContainsKey("entityId")
            ? default
            : await EntitiesHttp.ReadBodyAsync(http);
        var entityId = http.Request.Query["entityId"].ToString() is { Length: > 0 } qe ? qe : EntitiesHttp.StringProp(body, "entityId");
        var recordId = http.Request.Query["recordId"].ToString() is { Length: > 0 } qr ? qr : EntitiesHttp.StringProp(body, "recordId");
        if (string.IsNullOrEmpty(entityId) || string.IsNullOrEmpty(recordId)) return EntitiesHttp.Result(new { error = "Validation failed" }, 400);
        if (ctx!.OrganizationId is not { } targetOrg) return EntitiesHttp.Result(new { error = "Organization context is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var storage = await db.Set<CustomEntityStorage>().FirstOrDefaultAsync(s =>
            s.EntityType == entityId && s.EntityId == recordId && s.OrganizationId == targetOrg);
        if (storage is not null)
        {
            storage.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        return EntitiesHttp.Result(new { ok = true }, 200);
    }

    private static string? NormalizeRecordId(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        var low = s.ToLowerInvariant();
        if (low is "create" or "new" or "null" or "undefined") return null;
        return Uuid.IsMatch(s) ? s : null;
    }

    private static void MergeDoc(IDictionary<string, object?> item, string? docJson)
    {
        if (string.IsNullOrWhiteSpace(docJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(docJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (!item.ContainsKey(p.Name)) item[p.Name] = JsonValues.ToClr(p.Value);
        }
        catch { /* ignore malformed docs */ }
    }
}
