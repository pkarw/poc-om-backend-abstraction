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
/// PARITY-TODO (clean extension points, deferred to later ports): API interceptors, response enrichers,
/// exports, the CRUD list cache + <c>x-om-cache</c>/tag invalidation, mutation guards, sync before/after
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

        // Single-item shortcut: GET /api/{base}?id=<uuid> returns the record (or 404).
        if (query.SingleId is { } singleId)
            return await FetchSingleAsync(http, config, ctx!, singleId, query.WithDeleted);

        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();

        // Empty org scope → 200 empty envelope without touching the base table (spec 02 R27 / 03 R21).
        if (config.OrgScoped && ctx!.OrganizationIds is { Count: 0 })
            return Json(CrudListQueryParser.BuildEnvelope(Array.Empty<object>(), 0, query.Page, query.PageSize), 200);

        // Index-backed list (opt-in): resolve matching ids (incl. cf:<key> filter/sort) from the query
        // index, then load those base rows by id in index order (upstream queryEngine list path, R49).
        if (config.UseIndexList)
        {
            var indexQuery = services.GetRequiredService<ICrudIndexQuery>();
            var indexed = await indexQuery.ResolveListAsync(config.EntityType, query, ctx!);
            if (indexed is not null)
                return await BuildIndexedListAsync(http, config, ctx!, query, indexed);
        }

        var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
        q = ApplyScope(q, config, ctx!, query.WithDeleted);
        if (query.Ids.Count > 0) q = q.Where(BuildIdInPredicate(config.IdSelector, query.Ids));
        if (config.ApplyFilters is not null) q = config.ApplyFilters(q, query, ctx!);

        var total = await q.CountAsync();
        q = ApplySort(q, config, query);
        var rows = await q.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        var items = rows.Select(config.ProjectItem).ToList();
        var customFields = services.GetRequiredService<ICrudCustomFields>();
        await customFields.MergeIntoListItemsAsync(config.EntityType, items, ctx!);
        if (config.ListHook is not null) await config.ListHook(items, ctx!, http);

        var envelope = CrudListQueryParser.BuildEnvelope(items.Cast<object>().ToList(), total, query.Page, query.PageSize);
        return Json(envelope, 200);
    }

    /// <summary>
    /// Materialize an index-backed list page: load the base rows for the resolved ids (still applying
    /// scope/soft-delete for safety), re-order them to match the index sort, decorate with custom fields,
    /// and build the envelope using the index's total. The index owns paging + filter/sort semantics.
    /// </summary>
    private static async Task<IResult> BuildIndexedListAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, CrudListQuery query, CrudIndexQueryResult indexed) where TEntity : class
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();

        var order = indexed.RecordIds;
        List<TEntity> rows;
        if (order.Count == 0)
        {
            rows = new List<TEntity>();
        }
        else
        {
            var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
            q = ApplyScope(q, config, ctx, query.WithDeleted);
            q = q.Where(BuildIdInPredicate(config.IdSelector, order));
            var unordered = await q.ToListAsync();
            var idSelector = config.IdSelector.Compile();
            var byId = unordered.ToDictionary(idSelector);
            rows = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        var items = rows.Select(config.ProjectItem).ToList();
        var customFields = services.GetRequiredService<ICrudCustomFields>();
        await customFields.MergeIntoListItemsAsync(config.EntityType, items, ctx);
        if (config.ListHook is not null) await config.ListHook(items, ctx, http);

        var envelope = CrudListQueryParser.BuildEnvelope(items.Cast<object>().ToList(), indexed.Total, query.Page, query.PageSize);
        return Json(envelope, 200);
    }

    private static async Task<IResult> GetByPathAsync<TEntity>(HttpContext http, string id, CrudConfig<TEntity> config) where TEntity : class
    {
        var (ctx, denied) = await AuthorizeAsync(http, config.GetFeatures ?? config.ListFeatures);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var recordId)) return Json(new { error = "Not found" }, 404);
        var withDeleted = CrudListQueryParser.ParseBooleanToken(http.Request.Query["withDeleted"].ToString());
        return await FetchSingleAsync(http, config, ctx!, recordId, withDeleted);
    }

    private static async Task<IResult> FetchSingleAsync<TEntity>(
        HttpContext http, CrudConfig<TEntity> config, CommandContext ctx, Guid recordId, bool withDeleted) where TEntity : class
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();

        if (config.OrgScoped && ctx.OrganizationIds is { Count: 0 })
            return Json(new { error = "Not found" }, 404);

        var q = db.Set<TEntity>().AsNoTracking().AsQueryable();
        q = ApplyScope(q, config, ctx, withDeleted);
        q = q.Where(BuildIdEqualsPredicate(config.IdSelector, recordId));
        var entity = await q.FirstOrDefaultAsync();
        if (entity is null) return Json(new { error = "Not found" }, 404);

        var project = config.ProjectDetail ?? config.ProjectItem;
        var item = project(entity);
        var customFields = services.GetRequiredService<ICrudCustomFields>();
        await customFields.MergeIntoDetailAsync(config.EntityType, item, ctx);
        return Json(item, 200);
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
