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
/// Labels routes — the port of upstream <c>api/labels/*</c>. Labels are per-user + per-org
/// (<c>UNIQUE user_id,tenant,org,slug</c>). GET/POST use <c>customers.people.*</c>; assign/unassign
/// declare only requireAuth and resolve the required feature at runtime from the target entity's kind
/// (companies.manage for a company, else people.manage). assign returns 201 when the command created a
/// row, else 200 (contract ambiguity #3).
/// </summary>
public sealed class LabelsRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.people.view" };
    private static readonly string[] Manage = { "customers.people.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/labels", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapPost("/api/customers/labels", (Func<HttpContext, Task<IResult>>)CreateAsync);
        routes.MapPost("/api/customers/labels/assign", (Func<HttpContext, Task<IResult>>)(http => AssignAsync(http, assign: true)));
        routes.MapPost("/api/customers/labels/unassign", (Func<HttpContext, Task<IResult>>)(http => AssignAsync(http, assign: false)));
    }

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (ctx!.UserId is not { } userId) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);
        if (ctx.OrganizationId is not { } orgId) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = http.Request.Query;
        var page = int.TryParse(q["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(q["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 50;
        var search = q["search"].ToString();

        var query = db.Set<CustomerLabel>().AsNoTracking().Where(l => l.UserId == userId && l.TenantId == ctx.TenantId && l.OrganizationId == orgId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(l => l.Label.ToLower().Contains(term));
        }
        var total = await query.CountAsync();
        var rows = await query.OrderBy(l => l.Label).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var items = rows.Select(l => new { id = l.Id.ToString(), slug = l.Slug, label = l.Label }).ToList();

        var assignedIds = new List<string>();
        if (Guid.TryParse(q["entityId"], out var entityId))
        {
            var labelIds = await db.Set<CustomerLabelAssignment>().AsNoTracking()
                .Where(a => a.EntityId == entityId && a.UserId == userId)
                .Select(a => a.LabelId).ToListAsync();
            assignedIds = labelIds.Select(x => x.ToString()).ToList();
        }

        return CustomersHttp.Json(new { items, assignedIds, total, page, pageSize, totalPages = (int)Math.Ceiling(total / (double)pageSize) }, 200);
    }

    private static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (ctx!.UserId is not { } userId) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);
        if (ctx.OrganizationId is not { } orgId) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var label = CustomersHttp.Str(body, "label")?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 120) return CustomersHttp.Json(new { error = "Invalid input" }, 400);
        var slug = CustomersHttp.Str(body, "slug")?.Trim();
        if (string.IsNullOrEmpty(slug)) slug = Slugify.Label(label);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<LabelCreateInput, LabelResult>(
                "customers.labels.create", new LabelCreateInput(orgId, ctx.TenantId ?? Guid.Empty, userId, slug, label), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { id = r.Result.Id, slug = r.Result.Slug, label = r.Result.Label }, 201);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> AssignAsync(HttpContext http, bool assign)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, null);
        if (denied is not null) return denied;
        if (ctx!.UserId is not { } userId) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);
        if (ctx.OrganizationId is not { } orgId) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var labelId = CustomersHttp.GuidOf(body, "labelId");
        var entityId = CustomersHttp.GuidOf(body, "entityId");
        if (labelId is null || entityId is null) return CustomersHttp.Json(new { error = "Invalid input" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == entityId && e.DeletedAt == null && e.TenantId == ctx.TenantId);
        if (entity is null) return CustomersHttp.Json(new { error = "Entity not found" }, 404);

        var feature = entity.Kind == "company" ? "customers.companies.manage" : "customers.people.manage";
        if (!await CustomersHttp.HasFeatureAsync(http, ctx, feature)) return CustomersHttp.Json(new { error = "Forbidden", requiredFeatures = new[] { feature } }, 403);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var input = new LabelAssignInput(orgId, ctx.TenantId ?? Guid.Empty, userId, labelId.Value, entityId.Value);
            var r = await bus.ExecuteWithLog<LabelAssignInput, LabelAssignResult>(
                assign ? "customers.labels.assign" : "customers.labels.unassign", input, ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            var status = assign ? (r.Result.Created ? 201 : 200) : 200;
            return CustomersHttp.Json(new { id = r.Result.AssignmentId }, status);
        }
        catch (CommandHttpException) { return CustomersHttp.Json(new { error = $"Failed to {(assign ? "assign" : "unassign")} label" }, 500); }
    }
}
