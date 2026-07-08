using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.QueryIndex.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex.Api;

/// <summary>
/// The query_index admin surface (upstream api/status.ts / reindex.ts / purge.ts). Best-effort port:
///   - GET  /api/query_index/status  (query_index.status.view) — per-entity base-vs-indexed counts + recent errors/logs
///   - POST /api/query_index/reindex (query_index.reindex)      — re-project an entity's records synchronously
///   - POST /api/query_index/purge   (query_index.purge)        — drop an entity's index rows in scope
///
/// PARITY-TODO: the upstream status route computes coverage snapshots, job partitions, vector/fulltext
/// counts + the <c>x-om-partial-index</c> header, and reindex/purge queue persistent worker events.
/// </summary>
public static class QueryIndexRoutes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/query_index/status", (Func<HttpContext, Task<IResult>>)StatusAsync);
        routes.MapPost("/api/query_index/reindex", (Func<HttpContext, Task<IResult>>)ReindexAsync);
        routes.MapPost("/api/query_index/purge", (Func<HttpContext, Task<IResult>>)PurgeAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "query_index.status.view");
        if (denied is not null) return denied;
        if (ctx!.TenantId is null) return Result(new { error = "Tenant context is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var tenantId = ctx.TenantId;
        var orgIds = ctx.OrganizationIds;

        // Entity types with active custom-field defs in scope (mirrors upstream's cf-defs filter).
        var entityTypes = await db.Set<CustomFieldDef>().AsNoTracking()
            .Where(d => d.IsActive && d.DeletedAt == null)
            .Where(d => d.TenantId == null || d.TenantId == tenantId)
            .Select(d => d.EntityId)
            .Distinct()
            .ToListAsync();

        var items = new List<object>();
        foreach (var entityType in entityTypes)
        {
            var baseCount = await db.Set<CustomEntityStorage>().AsNoTracking()
                .CountAsync(s => s.EntityType == entityType && s.DeletedAt == null
                                 && (s.TenantId == null || s.TenantId == tenantId));
            var indexQuery = db.Set<EntityIndexRow>().AsNoTracking()
                .Where(r => r.EntityType == entityType && r.DeletedAt == null && r.TenantId == tenantId);
            if (orgIds is { Count: > 0 })
                indexQuery = indexQuery.Where(r => r.OrganizationId == null || orgIds.Contains(r.OrganizationId.Value));
            var indexCount = await indexQuery.CountAsync();

            items.Add(new
            {
                entityId = entityType,
                label = entityType,
                baseCount,
                indexCount,
                ok = baseCount == indexCount,
                job = new { status = "idle" },
                refreshedAt = (DateTimeOffset?)null,
            });
        }

        var errors = await db.Set<IndexerErrorLog>().AsNoTracking()
            .Where(e => e.TenantId == null || e.TenantId == tenantId)
            .OrderByDescending(e => e.OccurredAt).Take(100)
            .Select(e => new { id = e.Id, e.Source, e.Handler, entityType = e.EntityType, recordId = e.RecordId, e.Message, occurredAt = e.OccurredAt })
            .ToListAsync();

        var logs = await db.Set<IndexerStatusLog>().AsNoTracking()
            .Where(l => l.TenantId == null || l.TenantId == tenantId)
            .OrderByDescending(l => l.OccurredAt).Take(100)
            .Select(l => new { id = l.Id, l.Source, l.Handler, l.Level, entityType = l.EntityType, l.Message, occurredAt = l.OccurredAt })
            .ToListAsync();

        return Result(new { items, errors, logs }, 200);
    }

    private static async Task<IResult> ReindexAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "query_index.reindex");
        if (denied is not null) return denied;

        var body = await ReadBodyAsync(http);
        var entityType = StringProp(body, "entityType");
        if (string.IsNullOrEmpty(entityType)) return Result(new { error = "Missing entityType" }, 400);
        if (!Reindexer.IsValidEntityIdShape(entityType)) return Result(new { error = "Invalid entityType" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var indexer = http.RequestServices.GetRequiredService<ICrudIndexer>();
        var processed = await Reindexer.ReindexEntityAsync(db, indexer, entityType!, ctx!.TenantId);
        return Result(new { ok = true, processed }, 200);
    }

    private static async Task<IResult> PurgeAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "query_index.purge");
        if (denied is not null) return denied;

        var body = await ReadBodyAsync(http);
        var entityType = StringProp(body, "entityType");
        if (string.IsNullOrEmpty(entityType)) return Result(new { error = "Missing entityType" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var purged = await Reindexer.PurgeEntityAsync(db, entityType!, ctx!.TenantId, ctx.OrganizationIds);
        return Result(new { ok = true, purged }, 200);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, params string[] features)
    {
        var bridge = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await bridge.ResolveAsync(http);
        if (ctx is null) return (null, Result(new { error = "Unauthorized" }, 401));
        if (features.Length > 0 && !await bridge.HasAllFeaturesAsync(ctx, features))
            return (null, Result(new { error = "Forbidden", requiredFeatures = features }, 403));
        return (ctx, null);
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext http)
    {
        try
        {
            if (http.Request.ContentLength is 0) return Empty();
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return Empty(); }
    }

    private static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private static string? StringProp(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IResult Result(object body, int status) => Results.Json(body, Json, statusCode: status);
}
