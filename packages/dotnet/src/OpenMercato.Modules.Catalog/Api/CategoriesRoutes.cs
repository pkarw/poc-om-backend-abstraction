using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Categories — the port of upstream <c>api/categories/route.ts</c>. The GET is hand-written: it loads
/// the in-scope categories, computes the materialized-path hierarchy (<see cref="CategoryHierarchy"/>)
/// and serves either a flat <c>manage</c> view (paged rows with parentName/childCount/descendantCount/
/// pathLabel) or a nested <c>tree</c> view. POST/PUT/DELETE reuse the CRUD factory (with
/// <c>MapList=false</c> so this custom GET owns the list path) and dispatch to
/// <c>catalog.categories.*</c>, which rebuild the tree columns on every write.
/// </summary>
public sealed class CategoriesRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.categories.view" };
    private static readonly string[] Manage = { "catalog.categories.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, Config());
        routes.MapGet("/api/catalog/categories", (Func<HttpContext, Task<IResult>>)ListAsync);
    }

    internal static CrudConfig<CatalogProductCategory> Config() => new()
    {
        BasePath = "catalog/categories",
        EntityType = CatalogIndexEntity.Category,
        ResourceKind = "catalog.category",
        MapList = false,   // hand-written hierarchy GET below owns the list path
        MapItemGet = false,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = c => c.Id,
        DeletedAtSelector = c => c.DeletedAt,
        TenantIdSelector = c => c.TenantId,
        OrganizationIdSelector = c => c.OrganizationId,
        // Unused (MapList=false) but required by the config; the hierarchy GET projects rows itself.
        ProjectItem = c => new Dictionary<string, object?> { ["id"] = c.Id.ToString(), ["name"] = c.Name },
        CreatedEvent = "catalog.category.created",
        UpdatedEvent = "catalog.category.updated",
        DeletedEvent = "catalog.category.deleted",
        ValidateCreate = Data.CatalogValidators.Category,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<CategoryCreateInput, CategoryResult>(
                "catalog.categories.create",
                new CategoryCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.CategoryId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<CategoryUpdateInput, CategoryResult>(
                "catalog.categories.update", new CategoryUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.CategoryId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Record identifier is required");
            var r = await m.Bus.ExecuteWithLog<CategoryDeleteInput, CategoryResult>(
                "catalog.categories.delete", new CategoryDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.CategoryId, r.LogEntry);
        },
    };

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await CatalogHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;

        var q = http.Request.Query;
        var view = (q["view"].ToString() ?? "manage").Trim().ToLowerInvariant();
        if (view is not ("manage" or "tree")) view = "manage";
        var page = ParseInt(q["page"], 1, 1, int.MaxValue);
        var pageSize = ParseInt(q["pageSize"], 50, 1, 200);
        var search = q["search"].ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        var status = (q["status"].ToString() ?? "all").Trim().ToLowerInvariant();
        var idSet = ParseIds(q["ids"].ToString());

        if (ctx!.TenantId is not { } tenantId)
            return CatalogHttp.Json(new { items = Array.Empty<object>(), error = "Tenant context is required." }, 400);
        if (ctx.OrganizationId is not { } organizationId)
            return CatalogHttp.Json(new { items = Array.Empty<object>(), error = "Organization context is required." }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var categories = await db.Set<CatalogProductCategory>().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.TenantId == tenantId && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToListAsync();
        var hierarchy = CategoryHierarchy.Compute(categories);
        var categoryById = categories.ToDictionary(c => c.Id.ToString(), StringComparer.Ordinal);

        if (view == "tree")
        {
            var nodeById = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            var roots = new List<Dictionary<string, object?>>();
            foreach (var entry in hierarchy.Ordered)
            {
                var node = new Dictionary<string, object?>
                {
                    ["id"] = entry.Id,
                    ["name"] = entry.Name,
                    ["parentId"] = entry.ParentId,
                    ["depth"] = entry.Depth,
                    ["pathLabel"] = entry.PathLabel,
                    ["ancestorIds"] = entry.AncestorIds,
                    ["childIds"] = entry.ChildIds,
                    ["descendantIds"] = entry.DescendantIds,
                    ["isActive"] = entry.IsActive,
                    ["children"] = new List<Dictionary<string, object?>>(),
                };
                nodeById[entry.Id] = node;
                if (entry.ParentId is { } pid && nodeById.TryGetValue(pid, out var parent))
                    ((List<Dictionary<string, object?>>)parent["children"]!).Add(node);
                else
                    roots.Add(node);
            }
            return CatalogHttp.Json(new { items = roots }, 200);
        }

        IEnumerable<CategoryHierarchy.Node> rows = hierarchy.Ordered;
        if (status == "active") rows = rows.Where(n => n.IsActive);
        else if (status == "inactive") rows = rows.Where(n => !n.IsActive);
        if (!string.IsNullOrEmpty(search))
            rows = rows.Where(n => n.Name.ToLowerInvariant().Contains(search) || n.PathLabel.ToLowerInvariant().Contains(search));
        if (idSet is not null) rows = rows.Where(n => idSet.Contains(n.Id));

        var rowList = rows.ToList();
        var total = rowList.Count;
        var paged = rowList.Skip((page - 1) * pageSize).Take(pageSize);

        var items = new List<Dictionary<string, object?>>();
        foreach (var node in paged)
        {
            categoryById.TryGetValue(node.Id, out var category);
            string? parentName = node.ParentId is { } pid && hierarchy.Map.TryGetValue(pid, out var parent) ? parent.Name : null;
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["name"] = node.Name,
                ["slug"] = category?.Slug,
                ["description"] = category?.Description,
                ["parentId"] = node.ParentId,
                ["parentName"] = parentName,
                ["depth"] = node.Depth,
                ["treePath"] = node.TreePath,
                ["pathLabel"] = node.PathLabel,
                ["childCount"] = node.ChildIds.Count,
                ["descendantCount"] = node.DescendantIds.Count,
                ["isActive"] = node.IsActive,
                ["updatedAt"] = category is null ? null : CatalogHttp.Iso(category.UpdatedAt),
                ["organizationId"] = organizationId.ToString(),
                ["tenantId"] = tenantId.ToString(),
            });
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return CatalogHttp.Json(new
        {
            items,
            total,
            page,
            pageSize,
            totalPages,
            organizationId = organizationId.ToString(),
            tenantId = tenantId.ToString(),
        }, 200);
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }

    private static HashSet<string>? ParseIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        var set = new HashSet<string>(parts, StringComparer.Ordinal);
        return set.Count > 0 ? set : null;
    }
}
