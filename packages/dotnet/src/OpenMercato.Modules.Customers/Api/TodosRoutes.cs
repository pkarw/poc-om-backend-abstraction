using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Lib;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Todos routes — DEPRECATED compatibility bridge (sunset 2026-06-30), port of upstream
/// <c>api/todos/*</c>. GET is feature-gated by <c>customers.view</c>; writes by
/// <c>customers.interactions.manage</c>. Writes delegate to the canonical <c>customers.interactions.*</c>
/// commands (interactionType <c>task</c>, source <c>adapter:todo</c>). POST returns 201
/// <c>{linkId, todoId}</c> (both the interaction id); PUT/DELETE return <c>{ok:true}</c>. All responses
/// carry the deprecation headers; the surface returns 410 when the legacy-adapters flag is off (dormant).
/// </summary>
public sealed class TodosRoutes : ICustomersRouteGroup
{
    private static readonly string[] ViewCustomers = { "customers.view" };
    private static readonly string[] Manage = { "customers.interactions.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/todos", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapPost("/api/customers/todos", (Func<HttpContext, Task<IResult>>)CreateAsync);
        routes.MapPut("/api/customers/todos", (Func<HttpContext, Task<IResult>>)UpdateAsync);
        routes.MapDelete("/api/customers/todos", (Func<HttpContext, Task<IResult>>)DeleteAsync);
    }

    private static IResult Deprecated(HttpContext http, object body, int status)
    {
        ActivitiesRoutes.SetDeprecationHeaders(http);
        return CustomersHttp.Json(body, status);
    }

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        // NOTE: customers.view is not a declared customers ACL feature (contract ambiguity); the RBAC
        // bridge treats an unknown feature per its own policy — reproduced verbatim from upstream.
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, ViewCustomers);
        if (denied is not null) { ActivitiesRoutes.SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var qp = http.Request.Query;
        var page = int.TryParse(qp["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(qp["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 50;
        var exportAll = CrudListQueryParser.ParseBooleanToken(qp["all"].ToString());
        Guid? entityFilter = Guid.TryParse(qp["entityId"], out var ef) ? ef : null;
        var search = qp["search"].ToString();

        var db = http.RequestServices.GetRequiredService<AppDbContext>();

        // Bridged canonical rows (adapter:todo source) + legacy links (deduped).
        var canonicalQ = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.InteractionType == "task" && i.Source == InteractionCompat.TodoAdapterSource && i.TenantId == ctx.TenantId && i.DeletedAt == null);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds) canonicalQ = canonicalQ.Where(i => orgIds.Contains(i.OrganizationId));
        if (entityFilter is { } efid) canonicalQ = canonicalQ.Where(i => i.EntityId == efid);
        var canonical = await canonicalQ.ToListAsync();
        var bridgeIds = canonical.Select(i => i.Id).ToHashSet();

        var legacyQ = db.Set<CustomerTodoLink>().AsNoTracking().Where(t => t.TenantId == ctx.TenantId);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds2) legacyQ = legacyQ.Where(t => orgIds2.Contains(t.OrganizationId));
        if (entityFilter is { } efid2) legacyQ = legacyQ.Where(t => t.EntityId == efid2);
        var legacy = await legacyQ.ToListAsync();

        var rows = new List<Dictionary<string, object?>>();
        rows.AddRange(canonical.Select(MapCanonical));
        rows.AddRange(legacy.Where(l => !bridgeIds.Contains(l.TodoId)).Select(MapLegacy));
        await AttachCustomersAsync(db, rows);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r => (r["todoTitle"]?.ToString() ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        rows = rows.OrderByDescending(r => r["createdAt"]?.ToString(), StringComparer.Ordinal).ToList();

        var total = rows.Count;
        var items = exportAll ? rows : rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Deprecated(http, new
        {
            items,
            total,
            page = exportAll ? 1 : page,
            pageSize = exportAll ? items.Count : pageSize,
            totalPages = exportAll ? 1 : Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
        }, 200);
    }

    private static Dictionary<string, object?> MapCanonical(CustomerInteraction i) => new()
    {
        ["id"] = i.Id.ToString(), ["todoId"] = i.Id.ToString(), ["todoSource"] = InteractionCompat.TaskSource,
        ["todoTitle"] = i.Title, ["todoIsDone"] = i.Status == "done", ["todoPriority"] = i.Priority,
        ["todoSeverity"] = (string?)null, ["todoDescription"] = i.Body, ["todoDueAt"] = CustomersHttp.Iso(i.ScheduledAt),
        ["todoOrganizationId"] = (string?)null, ["organizationId"] = i.OrganizationId.ToString(),
        ["tenantId"] = i.TenantId.ToString(), ["createdAt"] = CustomersHttp.Iso(i.CreatedAt), ["_entityId"] = i.EntityId.ToString(),
    };

    private static Dictionary<string, object?> MapLegacy(CustomerTodoLink l) => new()
    {
        ["id"] = l.Id.ToString(), ["todoId"] = l.TodoId.ToString(), ["todoSource"] = l.TodoSource,
        ["todoTitle"] = (string?)null, ["todoIsDone"] = (bool?)null, ["todoPriority"] = (int?)null,
        ["todoSeverity"] = (string?)null, ["todoDescription"] = (string?)null, ["todoDueAt"] = (string?)null,
        ["todoOrganizationId"] = l.OrganizationId.ToString(), ["organizationId"] = l.OrganizationId.ToString(),
        ["tenantId"] = l.TenantId.ToString(), ["createdAt"] = CustomersHttp.Iso(l.CreatedAt), ["_entityId"] = l.EntityId.ToString(),
    };

    private static async Task AttachCustomersAsync(AppDbContext db, List<Dictionary<string, object?>> rows)
    {
        var ids = rows.Select(r => Guid.TryParse(r["_entityId"]?.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        var byId = ids.Count == 0 ? new Dictionary<Guid, CustomerEntity>()
            : (await db.Set<CustomerEntity>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync()).ToDictionary(e => e.Id);
        foreach (var r in rows)
        {
            var eid = Guid.TryParse(r["_entityId"]?.ToString(), out var g) ? (Guid?)g : null;
            var c = eid is { } id && byId.TryGetValue(id, out var cust) ? cust : null;
            r["customer"] = new { id = c?.Id.ToString(), displayName = c?.DisplayName, kind = c?.Kind };
            r.Remove("_entityId");
        }
    }

    private static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { ActivitiesRoutes.SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        if (CustomersHttp.GuidOf(body, "entityId") is null) return Deprecated(http, new { error = "Validation failed" }, 400);
        var isDone = CustomersHttp.Bool(body, "isDone") ?? CustomersHttp.Bool(body, "is_done") ?? false;
        var payload = JsonSerializer.Serialize(new
        {
            entityId = CustomersHttp.Str(body, "entityId"),
            interactionType = "task",
            title = CustomersHttp.Str(body, "title"),
            status = isDone ? "done" : "planned",
            source = InteractionCompat.TodoAdapterSource,
        }, CustomersHttp.Web);
        var input = JsonDocument.Parse(payload).RootElement.Clone();
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCreateInput, InteractionResult>(
                "customers.interactions.create", new InteractionCreateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, input), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "created");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { linkId = r.Result.InteractionId, todoId = r.Result.InteractionId }, 201);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { ActivitiesRoutes.SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var target = await ResolveTargetAsync(http, ctx!, CustomersHttp.GuidOf(body, "id"), CustomersHttp.GuidOf(body, "linkId"));
        if (target is null) return Deprecated(http, new { error = "Todo not found" }, 404);

        var nextDone = CustomersHttp.Bool(body, "isDone") ?? CustomersHttp.Bool(body, "is_done");
        var patch = JsonSerializer.Serialize(new
        {
            id = target.Value.ToString(),
            title = CustomersHttp.Has(body, "title") ? CustomersHttp.Str(body, "title") : null,
            status = nextDone is { } nd ? (nd ? "done" : "planned") : null,
        }, CustomersHttp.Web);
        var input = JsonDocument.Parse(patch).RootElement.Clone();
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionUpdateInput, InteractionResult>(
                "customers.interactions.update", new InteractionUpdateInput(target.Value, input), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { ActivitiesRoutes.SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) return Deprecated(http, new { error = "Validation failed" }, 400);
        var target = await ResolveTargetAsync(http, ctx!, CustomersHttp.GuidOf(body, "todoId") ?? id, id);
        if (target is null) return Deprecated(http, new { error = "Todo not found" }, 404);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionDeleteInput, InteractionResult>(
                "customers.interactions.delete", new InteractionDeleteInput(target.Value), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "deleted");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }

    /// <summary>
    /// Resolve the canonical interaction id a todo write targets — port of
    /// <c>resolveCanonicalTodoTargetId</c>. When <paramref name="todoId"/> is already a canonical row
    /// use it; else find the legacy <c>customer_todo_links</c> row and bridge it into a canonical
    /// interaction (source <c>adapter:todo</c>) reusing the link's todo_id.
    /// </summary>
    private static async Task<Guid?> ResolveTargetAsync(HttpContext http, CommandContext ctx, Guid? todoId, Guid? linkId)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        if (todoId is { } tid && await db.Set<CustomerInteraction>().AnyAsync(i => i.Id == tid && i.TenantId == ctx.TenantId && i.DeletedAt == null))
            return tid;

        CustomerTodoLink? link = null;
        if (linkId is { } lid) link = await db.Set<CustomerTodoLink>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == lid && t.TenantId == ctx.TenantId);
        if (link is null && todoId is { } tid2) link = await db.Set<CustomerTodoLink>().AsNoTracking().FirstOrDefaultAsync(t => t.TodoId == tid2 && t.TenantId == ctx.TenantId);
        if (link is null) return todoId; // no legacy row → surface the caller's id (update will 404 if absent)

        // Bridge: create a canonical interaction reusing the link's todo_id.
        var payload = JsonSerializer.Serialize(new
        {
            id = link.TodoId.ToString(), entityId = link.EntityId.ToString(),
            interactionType = "task", status = "planned", source = InteractionCompat.TodoAdapterSource,
        }, CustomersHttp.Web);
        var input = JsonDocument.Parse(payload).RootElement.Clone();
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCreateInput, InteractionResult>(
                "customers.interactions.create", new InteractionCreateInput(link.OrganizationId, link.TenantId, input), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "created");
        }
        catch (CommandHttpException) { /* bridge best-effort; downstream update/delete will 404 if unresolved */ }
        return link.TodoId;
    }
}
