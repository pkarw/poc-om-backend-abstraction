using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Tags CRUD + assign/unassign — the port of upstream <c>api/tags/*</c>. CRUD via makeCrudRoute
/// (pageSize default 100; search → label ILIKE); reuses <c>customers.activities.*</c> features.
/// assign returns 201, unassign returns 200 (null id when nothing assigned) — the documented
/// 201-vs-200 asymmetry (contract ambiguity #3). Generic failure → 400 (not 500).
/// </summary>
public sealed class TagsRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.activities.view" };
    private static readonly string[] Manage = { "customers.activities.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, Config());
        routes.MapPost("/api/customers/tags/assign", (Func<HttpContext, Task<IResult>>)(http => AssignAsync(http, assign: true)));
        routes.MapPost("/api/customers/tags/unassign", (Func<HttpContext, Task<IResult>>)(http => AssignAsync(http, assign: false)));
    }

    internal static CrudConfig<CustomerTag> Config() => new()
    {
        BasePath = "customers/tags",
        EntityType = "customers:customer_tag",
        ResourceKind = "customers.tag",
        DefaultSortField = "label",
        DefaultPageSize = 100,
        SoftDelete = false,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = t => t.Id,
        TenantIdSelector = t => t.TenantId,
        OrganizationIdSelector = t => t.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CustomerTag>, bool, IOrderedQueryable<CustomerTag>>>
        {
            ["label"] = (q, d) => d ? q.OrderByDescending(t => t.Label) : q.OrderBy(t => t.Label),
            ["slug"] = (q, d) => d ? q.OrderByDescending(t => t.Slug) : q.OrderBy(t => t.Slug),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                q = q.Where(t => t.Label.ToLower().Contains(term));
            }
            return q;
        },
        ProjectItem = t => new Dictionary<string, object?>
        {
            ["id"] = t.Id.ToString(), ["slug"] = t.Slug, ["label"] = t.Label, ["color"] = t.Color,
            ["description"] = t.Description, ["organizationId"] = t.OrganizationId.ToString(), ["tenantId"] = t.TenantId.ToString(),
        },
        CreatedEvent = "customers.tag.created",
        UpdatedEvent = "customers.tag.updated",
        DeletedEvent = "customers.tag.deleted",
        ValidateCreate = Data.CustomersValidators.Tag,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<TagCreateInput, TagResult>(
                "customers.tags.create", new TagCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.TagId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<TagUpdateInput, TagResult>(
                "customers.tags.update", new TagUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.TagId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = PeopleRoutes.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Tag id is required");
            var r = await m.Bus.ExecuteWithLog<TagDeleteInput, TagResult>(
                "customers.tags.delete", new TagDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.TagId, r.LogEntry);
        },
    };

    private static async Task<IResult> AssignAsync(HttpContext http, bool assign)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        var body = await CustomersHttp.ReadBodyAsync(http);
        var tagId = CustomersHttp.GuidOf(body, "tagId");
        var entityId = CustomersHttp.GuidOf(body, "entityId");
        if (tagId is null || entityId is null)
            return CustomersHttp.Json(new { error = $"Failed to {(assign ? "assign" : "unassign")} tag" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var input = new TagAssignInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, tagId.Value, entityId.Value);
            var r = await bus.ExecuteWithLog<TagAssignInput, TagAssignResult>(
                assign ? "customers.tags.assign" : "customers.tags.unassign", input, ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { id = r.Result.AssignmentId }, assign ? 201 : 200);
        }
        catch (CommandHttpException) { return CustomersHttp.Json(new { error = $"Failed to {(assign ? "assign" : "unassign")} tag" }, 400); }
    }
}
