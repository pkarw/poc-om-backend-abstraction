using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Pipelines + pipeline-stages routes — the port of upstream <c>api/pipelines/*</c>,
/// <c>api/pipeline-stages/*</c> and <c>.../reorder</c>. Hand-written command-bus routes (non-undoable):
/// GET requires org+tenant context (400 <c>Organization and tenant context required</c>); writes dispatch
/// the <c>customers.pipelines.*</c> / <c>customers.pipeline-stages.*</c> commands and map 404/409
/// <see cref="CommandHttpException"/>s straight through, with any other failure collapsing to the generic
/// 400 <c>Failed to …</c> body (mirrors upstream's ZodError → generic-catch). Stage lists join the
/// <c>pipeline_stage</c> dictionary for color/icon.
/// </summary>
public sealed class PipelinesRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.pipelines.view" };
    private static readonly string[] Manage = { "customers.pipelines.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/pipelines", (Func<HttpContext, Task<IResult>>)PipelinesGetAsync);
        routes.MapPost("/api/customers/pipelines", (Func<HttpContext, Task<IResult>>)PipelinesPostAsync);
        routes.MapPut("/api/customers/pipelines", (Func<HttpContext, Task<IResult>>)PipelinesPutAsync);
        routes.MapDelete("/api/customers/pipelines", (Func<HttpContext, Task<IResult>>)PipelinesDeleteAsync);
        routes.MapGet("/api/customers/pipeline-stages", (Func<HttpContext, Task<IResult>>)StagesGetAsync);
        routes.MapPost("/api/customers/pipeline-stages", (Func<HttpContext, Task<IResult>>)StagesPostAsync);
        routes.MapPut("/api/customers/pipeline-stages", (Func<HttpContext, Task<IResult>>)StagesPutAsync);
        routes.MapDelete("/api/customers/pipeline-stages", (Func<HttpContext, Task<IResult>>)StagesDeleteAsync);
        routes.MapPost("/api/customers/pipeline-stages/reorder", (Func<HttpContext, Task<IResult>>)ReorderAsync);
    }

    // ---- pipelines ----------------------------------------------------------------------------

    private static async Task<IResult> PipelinesGetAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (ctx!.OrganizationId is not { } org || org == Guid.Empty || ctx.TenantId is not { } tenant)
            return CustomersHttp.Json(new { error = "Organization and tenant context required" }, 400);
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var q = db.Set<CustomerPipeline>().AsNoTracking().Where(p => p.OrganizationId == org && p.TenantId == tenant);
            var isDefault = http.Request.Query["isDefault"].ToString();
            if (isDefault == "true") q = q.Where(p => p.IsDefault);
            else if (isDefault == "false") q = q.Where(p => !p.IsDefault);
            var pipelines = await q.OrderBy(p => p.CreatedAt).ToListAsync();
            var items = pipelines.Select(p => new
            {
                id = p.Id.ToString(), name = p.Name, isDefault = p.IsDefault,
                organizationId = p.OrganizationId.ToString(), tenantId = p.TenantId.ToString(),
                createdAt = CustomersHttp.Iso(p.CreatedAt), updatedAt = CustomersHttp.Iso(p.UpdatedAt),
            }).ToList();
            return CustomersHttp.Json(new { items, total = items.Count }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = "Failed to load pipelines" }, 500); }
    }

    private static Task<IResult> PipelinesPostAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to create pipeline", async (ctx, body, bus) =>
    {
        var r = await bus.ExecuteWithLog<PipelineCreateInput, PipelineResult>(
            "customers.pipelines.create", new PipelineCreateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
        PeopleRoutes.SetOperationHeader(http, r.LogEntry);
        return CustomersHttp.Json(new { id = r.Result.PipelineId }, 201);
    });

    private static Task<IResult> PipelinesPutAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to update pipeline", async (ctx, body, bus) =>
    {
        var r = await bus.ExecuteWithLog<PipelineUpdateInput, PipelineResult>(
            "customers.pipelines.update", new PipelineUpdateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
        PeopleRoutes.SetOperationHeader(http, r.LogEntry);
        return CustomersHttp.Json(new { ok = true }, 200);
    });

    private static Task<IResult> PipelinesDeleteAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to delete pipeline", async (ctx, body, bus) =>
    {
        var id = CustomersHttp.GuidOf(body, "id") ?? throw new ArgumentException("id is required");
        await bus.ExecuteWithLog<PipelineDeleteInput, PipelineResult>(
            "customers.pipelines.delete", new PipelineDeleteInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, id), ctx);
        return CustomersHttp.Json(new { ok = true }, 200);
    });

    // ---- pipeline-stages ----------------------------------------------------------------------

    private static async Task<IResult> StagesGetAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (ctx!.OrganizationId is not { } org || org == Guid.Empty || ctx.TenantId is not { } tenant)
            return CustomersHttp.Json(new { error = "Organization and tenant context required" }, 400);
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var q = db.Set<CustomerPipelineStage>().AsNoTracking().Where(s => s.OrganizationId == org && s.TenantId == tenant);
            var pipelineIdRaw = http.Request.Query["pipelineId"].ToString();
            if (Guid.TryParse(pipelineIdRaw, out var pipelineId)) q = q.Where(s => s.PipelineId == pipelineId);
            var stages = await q.OrderBy(s => s.Order).ToListAsync();
            var labels = stages.Select(s => s.Label.Trim().ToLowerInvariant()).ToList();
            var appearances = await db.Set<CustomerDictionaryEntry>().AsNoTracking()
                .Where(e => e.OrganizationId == org && e.TenantId == tenant && e.Kind == "pipeline_stage" && labels.Contains(e.NormalizedValue))
                .ToDictionaryAsync(e => e.NormalizedValue);
            var items = stages.Select(s =>
            {
                var a = appearances.GetValueOrDefault(s.Label.Trim().ToLowerInvariant());
                return new
                {
                    id = s.Id.ToString(), pipelineId = s.PipelineId.ToString(), label = s.Label, order = s.Order,
                    color = a?.Color, icon = a?.Icon, organizationId = s.OrganizationId.ToString(), tenantId = s.TenantId.ToString(),
                    createdAt = CustomersHttp.Iso(s.CreatedAt), updatedAt = CustomersHttp.Iso(s.UpdatedAt),
                };
            }).ToList();
            return CustomersHttp.Json(new { items, total = items.Count }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = "Failed to load pipeline stages" }, 500); }
    }

    private static Task<IResult> StagesPostAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to create pipeline stage", async (ctx, body, bus) =>
    {
        var r = await bus.ExecuteWithLog<PipelineStageCreateInput, PipelineStageResult>(
            "customers.pipeline-stages.create", new PipelineStageCreateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
        PeopleRoutes.SetOperationHeader(http, r.LogEntry);
        return CustomersHttp.Json(new { id = r.Result.StageId }, 201);
    });

    private static Task<IResult> StagesPutAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to update pipeline stage", async (ctx, body, bus) =>
    {
        var r = await bus.ExecuteWithLog<PipelineStageUpdateInput, PipelineStageResult>(
            "customers.pipeline-stages.update", new PipelineStageUpdateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
        PeopleRoutes.SetOperationHeader(http, r.LogEntry);
        return CustomersHttp.Json(new { ok = true }, 200);
    });

    private static Task<IResult> StagesDeleteAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to delete pipeline stage", async (ctx, body, bus) =>
    {
        var id = CustomersHttp.GuidOf(body, "id") ?? throw new ArgumentException("id is required");
        await bus.ExecuteWithLog<PipelineStageDeleteInput, PipelineStageResult>(
            "customers.pipeline-stages.delete", new PipelineStageDeleteInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, id), ctx);
        return CustomersHttp.Json(new { ok = true }, 200);
    });

    private static Task<IResult> ReorderAsync(HttpContext http) => WriteAsync(http, Manage, "Failed to reorder pipeline stages", async (ctx, body, bus) =>
    {
        await bus.ExecuteWithLog<PipelineStageReorderInput, PipelineStageResult>(
            "customers.pipeline-stages.reorder", new PipelineStageReorderInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
        return CustomersHttp.Json(new { ok = true }, 200);
    });

    // ---- shared write wrapper -----------------------------------------------------------------

    private static async Task<IResult> WriteAsync(
        HttpContext http, string[] features, string genericError,
        Func<CommandContext, System.Text.Json.JsonElement, CommandBus, Task<IResult>> handler)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, features);
        if (denied is not null) return denied;
        try
        {
            var body = await CustomersHttp.ReadBodyAsync(http);
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            return await handler(ctx!, body, bus);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
        catch { return CustomersHttp.Json(new { error = genericError }, 400); }
    }
}
