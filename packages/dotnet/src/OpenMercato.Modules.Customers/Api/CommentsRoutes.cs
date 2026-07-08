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

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Comments routes — port of upstream <c>api/comments/*</c>. Factory-backed CRUD: paged list
/// (filter by entityId/dealId, dealTitle enrichment) + command-bus writes. Feature-gated by
/// <c>customers.activities.view</c>/<c>manage</c> (comments share the activities ACL upstream). POST
/// returns 201 <c>{id, authorUserId}</c>; PUT/DELETE return <c>{ok:true}</c>.
/// </summary>
public sealed class CommentsRoutes : ICustomersRouteGroup
{
    public const string EntityType = "customers:customer_comment";
    private static readonly string[] View = { "customers.activities.view" };
    private static readonly string[] Manage = { "customers.activities.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CustomerComment> Config() => new()
    {
        BasePath = "customers/comments",
        EntityType = EntityType,
        ResourceKind = "customers.comment",
        DefaultSortField = "createdAt",
        // Base-table list (comments are not a CE-registered index entity — same convention as tags).
        UseIndexList = false,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = c => c.Id,
        DeletedAtSelector = c => c.DeletedAt,
        TenantIdSelector = c => c.TenantId,
        OrganizationIdSelector = c => c.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CustomerComment>, bool, IOrderedQueryable<CustomerComment>>>
        {
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(c => c.UpdatedAt) : q.OrderBy(c => c.UpdatedAt),
        },
        ApplyFilters = ApplyFilters,
        ProjectItem = Project,
        ListHook = EnrichDealTitlesAsync,
        CreatedEvent = "customers.comment.created",
        UpdatedEvent = "customers.comment.updated",
        DeletedEvent = "customers.comment.deleted",
        ValidateCreate = ValidateCreate,
        CreateStatus = 201,
        CreateResponse = o => new { id = o.Id, authorUserId = (o.Result as CommentResult)?.AuthorUserId },
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<CommentCreateInput, CommentResult>(
                "customers.comments.create", new CommentCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.CommentId, r.LogEntry, r.Result);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<CommentUpdateInput, CommentResult>(
                "customers.comments.update", new CommentUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.CommentId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id");
            if (id is null && m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g)) id = g;
            if (id is null) throw CommandHttpException.BadRequest("Comment id is required");
            var r = await m.Bus.ExecuteWithLog<CommentDeleteInput, CommentResult>(
                "customers.comments.delete", new CommentDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.CommentId, r.LogEntry);
        },
    };

    private static IQueryable<CustomerComment> ApplyFilters(IQueryable<CustomerComment> q, CrudListQuery query, CommandContext ctx)
    {
        if (query.Filters.TryGetValue("entityId", out var e) && Guid.TryParse(e, out var eid)) q = q.Where(c => c.EntityId == eid);
        if (query.Filters.TryGetValue("dealId", out var d) && Guid.TryParse(d, out var did)) q = q.Where(c => c.DealId == did);
        return q;
    }

    internal static IDictionary<string, object?> Project(CustomerComment c) => new Dictionary<string, object?>
    {
        ["id"] = c.Id.ToString(),
        ["entityId"] = c.EntityId.ToString(),
        ["dealId"] = c.DealId?.ToString(),
        ["body"] = c.Body,
        ["authorUserId"] = c.AuthorUserId?.ToString(),
        ["appearanceIcon"] = c.AppearanceIcon,
        ["appearanceColor"] = c.AppearanceColor,
        ["organizationId"] = c.OrganizationId.ToString(),
        ["tenantId"] = c.TenantId.ToString(),
        ["createdAt"] = CustomersHttp.Iso(c.CreatedAt),
        ["updatedAt"] = CustomersHttp.Iso(c.UpdatedAt),
    };

    private static async Task EnrichDealTitlesAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var dealIds = items
            .Select(i => i.TryGetValue("dealId", out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();
        if (dealIds.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var titles = (await db.Set<CustomerDeal>().AsNoTracking().Where(d => dealIds.Contains(d.Id)).ToListAsync())
            .ToDictionary(d => d.Id, d => d.Title);
        foreach (var item in items)
            item["dealTitle"] = Guid.TryParse(item.TryGetValue("dealId", out var v) ? v?.ToString() : null, out var id) && titles.TryGetValue(id, out var t) ? t : null;
    }

    private static IReadOnlyList<CrudValidationIssue> ValidateCreate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (CustomersHttp.GuidOf(body, "entityId") is null)
            issues.Add(new CrudValidationIssue(new[] { "entityId" }, "entityId is required", "invalid_uuid"));
        var b = CustomersHttp.Str(body, "body")?.Trim();
        if (string.IsNullOrEmpty(b) || b.Length > 8000)
            issues.Add(new CrudValidationIssue(new[] { "body" }, "body is required", "invalid_string"));
        return issues;
    }
}
