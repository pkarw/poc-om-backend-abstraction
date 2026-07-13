using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Variants CRUD — the port of upstream <c>api/variants/route.ts</c>. Index-backed list (snake_case
/// DataQuery output); create/update/delete dispatch to <c>catalog.variants.*</c>. GET requires
/// <c>catalog.products.view</c>; mutations require <c>catalog.variants.manage</c>. A variant inherits its
/// organization/tenant scope from its parent product (resolved in the create command).
/// </summary>
public sealed class VariantsRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };
    private static readonly string[] Manage = { "catalog.variants.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogProductVariant> Config() => new()
    {
        BasePath = "catalog/variants",
        EntityType = CatalogIndexEntity.Variant,
        ResourceKind = "catalog.variant",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = v => v.Id,
        DeletedAtSelector = v => v.DeletedAt,
        TenantIdSelector = v => v.TenantId,
        OrganizationIdSelector = v => v.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogProductVariant>, bool, IOrderedQueryable<CatalogProductVariant>>>
        {
            ["name"] = (q, d) => d ? q.OrderByDescending(v => v.Name) : q.OrderBy(v => v.Name),
            ["sku"] = (q, d) => d ? q.OrderByDescending(v => v.Sku) : q.OrderBy(v => v.Sku),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(v => v.CreatedAt) : q.OrderBy(v => v.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(v => v.UpdatedAt) : q.OrderBy(v => v.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("productId", out var pid) && Guid.TryParse(pid, out var productId))
                q = q.Where(v => v.ProductId == productId);
            if (query.Filters.TryGetValue("sku", out var sku) && !string.IsNullOrWhiteSpace(sku))
                q = q.Where(v => v.Sku == sku.Trim());
            if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
                q = q.Where(v => v.IsActive == active);
            if (query.Filters.TryGetValue("isDefault", out var idf) && CatalogFilter.TryBool(idf, out var isDefault))
                q = q.Where(v => v.IsDefault == isDefault);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                q = q.Where(v =>
                    (v.Name != null && v.Name.ToLower().Contains(term)) ||
                    (v.Sku != null && v.Sku.ToLower().Contains(term)) ||
                    (v.Barcode != null && v.Barcode.ToLower().Contains(term)));
            }
            return q;
        },
        ProjectItem = v => new Dictionary<string, object?>(CatalogIndexBaseRowResolver.ProjectVariantDoc(v)),
        CreatedEvent = "catalog.variant.created",
        UpdatedEvent = "catalog.variant.updated",
        DeletedEvent = "catalog.variant.deleted",
        ValidateCreate = Data.CatalogValidators.Variant,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<VariantCreateInput, VariantResult>(
                "catalog.variants.create", new VariantCreateInput(m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.VariantId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<VariantUpdateInput, VariantResult>(
                "catalog.variants.update", new VariantUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.VariantId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Variant id is required");
            var r = await m.Bus.ExecuteWithLog<VariantDeleteInput, VariantResult>(
                "catalog.variants.delete", new VariantDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.VariantId, r.LogEntry);
        },
    };
}
