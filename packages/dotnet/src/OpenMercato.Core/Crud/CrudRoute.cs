using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;

namespace OpenMercato.Core.Crud;

/// <summary>
/// The <c>makeCrudRoute</c> equivalent — registers the 5 standard CRUD endpoints for an entity
/// (packages/shared/src/lib/crud/factory.ts). Called from a module's <c>MapRoutes</c>:
/// <code>CrudRoute.Map(routes, new CrudConfig&lt;Person&gt; { ... });</code>
///
/// Owns the observable pipeline for each method (spec 02 R7/R19–R43):
///   - GET list  <c>/api/{base}</c>       → <c>{items,total,page,pageSize,totalPages}</c> (or a single item when <c>?id=</c>)
///   - GET item  <c>/api/{base}/{id}</c>  → the record, or 404 <c>{error:"Not found"}</c>
///   - POST      <c>/api/{base}</c>       → 201 <c>{id}</c> + <c>x-om-operation</c> header
///   - PUT       <c>/api/{base}</c>       → 200 <c>{ok:true}</c>
///   - DELETE    <c>/api/{base}</c>       → 200 <c>{ok:true}</c>
///
/// Auth (401/403) is enforced via <see cref="ICrudRequestContext"/>; mutations dispatch through the
/// <see cref="CommandBus"/>; lifecycle events + <see cref="ICrudIndexer"/> upserts fire on success;
/// <see cref="ICrudCustomFields"/> decorates reads and persists <c>cf_*</c> writes;
/// <see cref="CommandHttpException"/> (incl. the optimistic-lock 409) maps straight to the response.
///
/// The list <c>?format=csv|json|xml|markdown</c> export (the OM "Export" button) serializes the full
/// filtered set via <see cref="CrudExport"/> (spec: factory GET export branch).
///
/// PARITY-TODO (clean extension points, deferred to later ports): API interceptors, response enrichers,
/// the CRUD list cache + <c>x-om-cache</c>/tag invalidation, mutation guards, sync before/after
/// event subscribers, and read-access logging. Each is a documented seam, not a behavioural change here.
/// </summary>
public static class CrudRoute
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    public static void Map<TEntity>(IEndpointRouteBuilder routes, CrudConfig<TEntity> config) where TEntity : class
    {
        var basePath = "/api/" + config.BasePath.Trim('/');

        // Cast handlers to Func<...,Task<IResult>> so minimal APIs treat them as route handlers that
        // write the returned IResult (a bare HttpContext->Task lambda is inferred as a RequestDelegate,
        // which discards the result — ASP0016).
        routes.MapGet(basePath, (Func<HttpContext, Task<IResult>>)(http => ListAsync(http, config)));
        if (config.MapItemGet)
            routes.MapGet(basePath + "/{id}", (Func<HttpContext, string, Task<IResult>>)((http, id) => GetByPathAsync(http, id, config)));
        routes.MapPost(basePath, (Func<HttpContext, Task<IResult>>)(http => CreateAsync(http, config)));
        routes.MapPut(basePath, (Func<HttpContext, Task<IResult>>)(http => MutateAsync(http, config, isDelete: false, config.UpdateDispatch, config.UpdateFeatures, config.ValidateUpdate, config.UpdateResponse)));
        routes.MapDelete(basePath, (Func<HttpContext, Task<IResult>>)(http => MutateAsync(http, config, isDelete: true, config.DeleteDispatch, config.DeleteFeatures, null, config.DeleteResponse)));
    }

    // ---- GET (list + single) ------------------------------------------------------------------

    private static async Task<IResult> ListAsync<TEntity>(HttpContext http, CrudConfig<TEntity> config) where TEntity : class
    {
        var (ctx, denied) = await AuthorizeAsync(http, config.ListFeatures);
        if (denied is not null) return denied;

        var query = CrudListQueryParser.Parse(http.Request, config.DefaultSortField, config.DefaultPageSize, config.MaxPageSize);

        // GET /api/{base}?id=<uuid> is a LIST filtered to one id — OM returns the standard
        // {items,total,...} envelope (items:[record] or []), NOT a bare record. (The bare-record
        // shape is only for the path form GET /api/{base}/{id}.) Caught by OM integration TC-CUR-001.
        if (query.SingleId is { } singleId)
        {
            var one = await FetchSingleRecordAsync(http, config, ctx!, singleId, query.WithDeleted);
            var oneItems = one is null ? new List<object>() : new List<object> { one };
            return Json(CrudListQueryParser.BuildEnvelope(oneItems, oneItems.Count, query.Page, query.PageSize), 200);
        }

        // Empty org scope → 200 empty envelope without touching the base table (spec 02 R27 / 03 R21).
        // Mirrors OM: even an export request on an empty scope returns the JSON envelope, not a file.
        if (config.OrgScoped && ctx!.OrganizationIds is { Count: 0 })
            return Json(CrudListQueryParser.BuildEnvelope(Array.Empty<object>(), 0, query.Page, query.PageSize), 200);

        // Export branch (the OM "Export" button): when ?format= names an enabled format, serialize the
        // FULL filtered result set instead of the paged JSON envelope (spec: factory GET handler).
        var exportFormat = ResolveExportFormat(config, http.Request);
        if (exportFormat is not null)
            return await ExportListAsync(http, config, ctx!, query, exportFormat);

        var (items, total) = await FetchListPageAsync(http, config, ctx!, query);
        if (config.ListHook is not null) await config.ListHook(items, ctx!, http);

        var envelope = CrudListQueryParser.BuildEnvelope(items.Cast<object>().ToList(), total, query.Page, query.PageSize);
        return Json(envelope, 200);
    }

    /// <summary>
    /// Fetch one list page — the shared read path for the normal list AND each export batch. Resolves via
    /// the query index when opted in (loading base rows by resolved id in index order), else via the base
    /// table with scope/soft-delete + filters + sort + pagination. Projects rows and decorates them with
    /// custom fields, so exported rows carry the exact same columns (snake_case + <c>cf_</c> keys) as the
    /// on-screen list. Does NOT run <see cref="CrudConfig{TEntity}.ListHook"/> — the caller owns that so it
    /// runs once (per page for the list, once over the aggregate for an export), matching OM's afterList.
    /// </summary>
    private static async Task<(List<IDictionary<string, object?>> Items, int Total)> FetchListPageAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, CrudListQuery query) where TEntity : class
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var customFields = services.GetRequiredService<ICrudCustomFields>();

        // Index-backed list (opt-in): resolve matching ids (incl. cf:<key> filter/sort) from the query
        // index, then load those base rows by id in index order (upstream queryEngine list path, R49).
        if (config.UseIndexList)
        {
            var indexQuery = services.GetRequiredService<ICrudIndexQuery>();
            var indexed = await indexQuery.ResolveListAsync(config.EntityType, query, ctx);
            if (indexed is not null)
            {
                var indexedRows = await LoadIndexedRowsAsync(db, config, ctx, query, indexed);
                var indexedItems = indexedRows.Select(config.ProjectItem).ToList();
                await customFields.MergeIntoListItemsAsync(config.EntityType, indexedItems, ctx);
                return (indexedItems, indexed.Total);
            }
        }

        var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
        q = ApplyScope(q, config, ctx, query.WithDeleted);
        if (query.Ids.Count > 0) q = q.Where(BuildIdInPredicate(config.IdSelector, query.Ids));
        if (config.ApplyFilters is not null) q = config.ApplyFilters(q, query, ctx);

        var total = await q.CountAsync();
        q = ApplySort(q, config, query);
        var rows = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        var items = rows.Select(config.ProjectItem).ToList();
        await customFields.MergeIntoListItemsAsync(config.EntityType, items, ctx);
        return (items, total);
    }

    /// <summary>
    /// Load the base rows for an index-resolved page: fetch by id (still applying scope/soft-delete for
    /// safety) then re-order them to match the index sort. The index owns paging + filter/sort semantics.
    /// </summary>
    private static async Task<List<TEntity>> LoadIndexedRowsAsync<TEntity>(
        AppDbContext db, CrudConfig<TEntity> config, CommandContext ctx, CrudListQuery query, CrudIndexQueryResult indexed) where TEntity : class
    {
        var order = indexed.RecordIds;
        if (order.Count == 0) return new List<TEntity>();

        var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
        q = ApplyScope(q, config, ctx, query.WithDeleted);
        q = q.Where(BuildIdInPredicate(config.IdSelector, order));
        var unordered = await q.ToListAsync();
        var idSelector = config.IdSelector.Compile();
        var byId = unordered.ToDictionary(idSelector);
        return order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    // ---- Export (the OM "Export" button) ------------------------------------------------------

    /// <summary>Total rows an export will materialize, capped to bound memory (mirrors OM's 10k batch ceiling).</summary>
    private const int MaxExportRows = 10000;

    /// <summary>Default export batch size (upstream DEFAULT_EXPORT_BATCH_SIZE); clamped to [100, 10000].</summary>
    private const int DefaultExportBatchSize = 1000;

    /// <summary>Resolve the requested export format if it is enabled for this resource, else null.</summary>
    private static string? ResolveExportFormat<TEntity>(CrudConfig<TEntity> config, HttpRequest request) where TEntity : class
    {
        var requested = CrudExport.NormalizeFormat(request.Query["format"].ToString());
        if (requested is null) return null;
        return ResolveAvailableFormats(config).Contains(requested) ? requested : null;
    }

    private static IReadOnlyList<string> ResolveAvailableFormats<TEntity>(CrudConfig<TEntity> config) where TEntity : class
    {
        // null (default) → all four formats; a provided list (even empty) is used verbatim after normalization.
        if (config.ExportFormats is null) return CrudExport.AllFormats;
        var result = new List<string>();
        foreach (var f in config.ExportFormats)
        {
            var n = CrudExport.NormalizeFormat(f);
            if (n is not null && !result.Contains(n)) result.Add(n);
        }
        return result;
    }

    /// <summary>
    /// Serialize the FULL filtered result set to a downloadable file: loop the shared list-fetch path
    /// page by page (reusing ProjectItem + custom-field decoration, so exported rows match the on-screen
    /// columns) until the total is reached or the <see cref="MaxExportRows"/> cap is hit, run the list hook
    /// once over the aggregate, then serialize via <see cref="CrudExport"/> with the right content type +
    /// Content-Disposition (upstream factory GET export branch).
    /// </summary>
    private static async Task<IResult> ExportListAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, CrudListQuery query, string format) where TEntity : class
    {
        var batchSize = Math.Min(Math.Max(Math.Max(query.PageSize, DefaultExportBatchSize), 100), MaxExportRows);

        var all = new List<IDictionary<string, object?>>();
        var page = 1;
        while (all.Count < MaxExportRows)
        {
            var pageQuery = query with { Page = page, PageSize = batchSize };
            var (items, total) = await FetchListPageAsync(http, config, ctx, pageQuery);
            if (items.Count == 0) break;
            all.AddRange(items);
            if (all.Count >= total) break;
            if (items.Count < batchSize) break;
            page++;
        }
        if (all.Count > MaxExportRows) all = all.GetRange(0, MaxExportRows);

        if (config.ListHook is not null) await config.ListHook(all, ctx, http);

        var rows = all.Select(d => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(d)).ToList();
        var prepared = CrudExport.PrepareDefault(rows);
        var serialized = CrudExport.Serialize(prepared, format);

        var fallbackBase = !string.IsNullOrWhiteSpace(config.ExportFilenameBase)
            ? config.ExportFilenameBase!
            : LastSegment(config.BasePath) ?? config.ResourceKind;
        var filename = CrudExport.DefaultFilename(fallbackBase, format);

        return new ExportFileResult(serialized.Body, serialized.ContentType, filename);
    }

    private static string? LastSegment(string basePath)
    {
        var trimmed = basePath.Trim('/');
        if (trimmed.Length == 0) return null;
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>Writes a UTF-8 export body with an exact content type + attachment Content-Disposition.</summary>
    private sealed class ExportFileResult : IResult
    {
        private readonly string _body;
        private readonly string _contentType;
        private readonly string _filename;

        public ExportFileResult(string body, string contentType, string filename)
        {
            _body = body;
            _contentType = contentType;
            _filename = filename;
        }

        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = _contentType;
            http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{_filename}\"";
            var bytes = System.Text.Encoding.UTF8.GetBytes(_body);
            http.Response.ContentLength = bytes.Length;
            await http.Response.Body.WriteAsync(bytes);
        }
    }

    private static async Task<IResult> GetByPathAsync<TEntity>(HttpContext http, string id, CrudConfig<TEntity> config) where TEntity : class
    {
        var (ctx, denied) = await AuthorizeAsync(http, config.GetFeatures ?? config.ListFeatures);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var recordId)) return Json(new { error = "Not found" }, 404);
        var withDeleted = CrudListQueryParser.ParseBooleanToken(http.Request.Query["withDeleted"].ToString());
        return await FetchSingleAsync(http, config, ctx!, recordId, withDeleted);
    }

    // Bare-record form: GET /api/{base}/{id} → the projected record, or 404.
    private static async Task<IResult> FetchSingleAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, Guid recordId, bool withDeleted) where TEntity : class
    {
        var item = await FetchSingleRecordAsync(http, config, ctx, recordId, withDeleted);
        return item is null ? Json(new { error = "Not found" }, 404) : Json(item, 200);
    }

    // Fetch + project a single record (or null), shared by the ?id= list-filter form and the path form.
    private static async Task<IDictionary<string, object?>?> FetchSingleRecordAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, Guid recordId, bool withDeleted) where TEntity : class
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();

        if (config.OrgScoped && ctx.OrganizationIds is { Count: 0 })
            return null;

        var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
        q = ApplyScope(q, config, ctx, withDeleted);
        q = q.Where(BuildIdEqualsPredicate(config.IdSelector, recordId));
        var entity = await q.FirstOrDefaultAsync();
        if (entity is null) return null;

        var project = config.ProjectDetail ?? config.ProjectItem;
        var item = project(entity);
        var customFields = services.GetRequiredService<ICrudCustomFields>();
        await customFields.MergeIntoDetailAsync(config.EntityType, item, ctx);
        return item;
    }

    // ---- POST (create) ------------------------------------------------------------------------

    private static Task<IResult> CreateAsync<TEntity>(HttpContext http, CrudConfig<TEntity> config) where TEntity : class
        => MutateAsync(http, config, isDelete: false, config.CreateDispatch, config.CreateFeatures, config.ValidateCreate, config.CreateResponse, isCreate: true);

    // ---- Shared mutation pipeline (POST/PUT/DELETE) -------------------------------------------

    private static async Task<IResult> MutateAsync<TEntity>(
        HttpContext http,
        CrudConfig<TEntity> config,
        bool isDelete,
        CrudDispatch? dispatch,
        string[]? features,
        Func<JsonElement, IReadOnlyList<CrudValidationIssue>>? validate,
        Func<CrudMutationOutcome, object>? responseBuilder,
        bool isCreate = false) where TEntity : class
    {
        if (dispatch is null) return Json(new { error = "Not implemented" }, 501);

        var (ctx, denied) = await AuthorizeAsync(http, features);
        if (denied is not null) return denied;

        // Mutation with empty org scope → 403 (spec 02 R32), distinct from GET's 200-empty.
        if (config.OrgScoped && ctx!.OrganizationIds is { Count: 0 })
            return Json(new { error = "Forbidden" }, 403);

        var body = await ReadBodyAsync(http);
        if (validate is not null)
        {
            var issues = validate(body);
            if (issues.Count > 0)
                return Json(new { error = "Invalid input", details = issues }, 400);
        }

        var services = http.RequestServices;
        var bus = services.GetRequiredService<CommandBus>();
        var query = http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        try
        {
            var outcome = await dispatch(new CrudMutationContext(http, ctx!, body, query, bus, services));

            // Side effects after the command's DB work (spec 03 R58): CRUD event + query-index projection.
            await EmitSideEffectsAsync(services, config, ctx!, outcome.Id, isDelete, isCreate);

            var payload = responseBuilder is not null
                ? responseBuilder(outcome)
                : outcome.Result ?? DefaultResponse(isCreate, outcome);
            var status = isCreate ? config.CreateStatus : 200;

            var header = BuildOperationHeader(outcome.Log);
            if (header is not null) http.Response.Headers["x-om-operation"] = header;
            return Json(payload, status);
        }
        catch (CommandHttpException ex)
        {
            return Json(ex.Body, ex.Status);
        }
    }

    private static object DefaultResponse(bool isCreate, CrudMutationOutcome outcome)
        => isCreate ? new { id = outcome.Id } : new { ok = true };

    private static async Task EmitSideEffectsAsync<TEntity>(
        IServiceProvider services, CrudConfig<TEntity> config, CommandContext ctx, string? id, bool isDelete, bool isCreate) where TEntity : class
    {
        if (string.IsNullOrEmpty(id)) return;

        var indexer = services.GetRequiredService<ICrudIndexer>();
        if (isDelete)
            await indexer.DeleteOneAsync(config.EntityType, id!, ctx.OrganizationId, ctx.TenantId);
        else
            await indexer.UpsertOneAsync(config.EntityType, id!, ctx.OrganizationId, ctx.TenantId, isCreate ? "create" : "update");

        var eventId = isDelete ? config.DeletedEvent : isCreate ? config.CreatedEvent : config.UpdatedEvent;
        if (!string.IsNullOrEmpty(eventId))
        {
            var events = services.GetRequiredService<IEventBus>();
            await events.PublishAsync(eventId!, new
            {
                id,
                organizationId = ctx.OrganizationId,
                tenantId = ctx.TenantId,
            });
        }
    }

    // ---- Auth ---------------------------------------------------------------------------------

    private static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, string[]? features)
    {
        var requestContext = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await requestContext.ResolveAsync(http);
        if (ctx is null) return (null, Json(new { error = "Unauthorized" }, 401));
        if (features is { Length: > 0 })
        {
            var ok = await requestContext.HasAllFeaturesAsync(ctx, features);
            if (!ok) return (null, Json(new { error = "Forbidden", requiredFeatures = features }, 403));
        }
        return (ctx, null);
    }

    // ---- Query composition helpers ------------------------------------------------------------

    private static IQueryable<TEntity> ApplyScope<TEntity>(
        IQueryable<TEntity> q, CrudConfig<TEntity> config, CommandContext ctx, bool withDeleted) where TEntity : class
    {
        if (config.SoftDelete && config.DeletedAtSelector is not null && !withDeleted)
            q = q.Where(BuildIsNullPredicate(config.DeletedAtSelector));

        if (config.TenantIdSelector is not null && ctx.TenantId is { } tenantId)
            q = q.Where(BuildNullableEqualsPredicate(config.TenantIdSelector, tenantId));

        if (config.OrgScoped && config.OrganizationIdSelector is not null && ctx.OrganizationIds is { Count: > 0 } orgIds)
            q = q.Where(BuildNullableInPredicate(config.OrganizationIdSelector, orgIds));

        return q;
    }

    private static IQueryable<TEntity> ApplySort<TEntity>(
        IQueryable<TEntity> q, CrudConfig<TEntity> config, CrudListQuery query) where TEntity : class
    {
        if (config.Sorts is not null && config.Sorts.TryGetValue(query.SortField, out var sorter))
            return sorter(q, query.SortDescending);
        // Default: stable order by the base id (upstream sort fallback is 'id').
        return query.SortDescending ? q.OrderByDescending(config.IdSelector) : q.OrderBy(config.IdSelector);
    }

    private static Expression<Func<TEntity, bool>> BuildIsNullPredicate<TEntity>(Expression<Func<TEntity, DateTimeOffset?>> selector)
    {
        var body = Expression.Equal(selector.Body, Expression.Constant(null, typeof(DateTimeOffset?)));
        return Expression.Lambda<Func<TEntity, bool>>(body, selector.Parameters);
    }

    private static Expression<Func<TEntity, bool>> BuildNullableEqualsPredicate<TEntity>(Expression<Func<TEntity, Guid?>> selector, Guid value)
    {
        var body = Expression.Equal(selector.Body, Expression.Constant((Guid?)value, typeof(Guid?)));
        return Expression.Lambda<Func<TEntity, bool>>(body, selector.Parameters);
    }

    private static Expression<Func<TEntity, bool>> BuildNullableInPredicate<TEntity>(Expression<Func<TEntity, Guid?>> selector, IReadOnlyList<Guid> ids)
    {
        var list = ids.Select(g => (Guid?)g).ToList();
        var containsMethod = typeof(List<Guid?>).GetMethod("Contains", new[] { typeof(Guid?) })!;
        var body = Expression.Call(Expression.Constant(list), containsMethod, selector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(body, selector.Parameters);
    }

    private static Expression<Func<TEntity, bool>> BuildIdInPredicate<TEntity>(Expression<Func<TEntity, Guid>> selector, IReadOnlyList<Guid> ids)
    {
        var list = ids.ToList();
        var containsMethod = typeof(List<Guid>).GetMethod("Contains", new[] { typeof(Guid) })!;
        var body = Expression.Call(Expression.Constant(list), containsMethod, selector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(body, selector.Parameters);
    }

    private static Expression<Func<TEntity, bool>> BuildIdEqualsPredicate<TEntity>(Expression<Func<TEntity, Guid>> selector, Guid value)
    {
        var body = Expression.Equal(selector.Body, Expression.Constant(value, typeof(Guid)));
        return Expression.Lambda<Func<TEntity, bool>>(body, selector.Parameters);
    }

    // ---- Response helpers ---------------------------------------------------------------------

    private static async Task<JsonElement> ReadBodyAsync(HttpContext http)
    {
        // Malformed / empty JSON is treated as {} (spec 02 R33) — never a raw 500.
        try
        {
            if (http.Request.ContentLength is 0) return EmptyObject();
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Build the <c>x-om-operation</c> header (spec 02 R41): <c>omop:</c> + url-encoded JSON of
    /// <c>{id,undoToken,commandId,actionLabel,resourceKind,resourceId,executedAt}</c>. Only when the
    /// log row carries an undo token (undoable operation) — the port of upstream
    /// <c>serializeOperationMetadata</c>.
    /// </summary>
    private static string? BuildOperationHeader(ActionLog? log)
    {
        if (log is null || string.IsNullOrEmpty(log.UndoToken) || string.IsNullOrEmpty(log.CommandId)) return null;
        var payload = new
        {
            id = log.Id.ToString(),
            undoToken = log.UndoToken,
            commandId = log.CommandId,
            actionLabel = log.ActionLabel,
            resourceKind = log.ResourceKind,
            resourceId = log.ResourceId,
            executedAt = (log.CreatedAt == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(log.CreatedAt, DateTimeKind.Utc)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        var json = JsonSerializer.Serialize(payload, JsonWeb);
        return "omop:" + Uri.EscapeDataString(json);
    }

    private static IResult Json(object body, int status) => Results.Json(body, JsonWeb, statusCode: status);
}
