using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Product unit-conversions CRUD — the port of upstream <c>api/product-unit-conversions/route.ts</c>.
/// Index-backed list (snake_case + a <c>unitCode</c> camelCase alias); GET requires
/// <c>catalog.products.view</c>, mutations <c>catalog.products.manage</c>. Dispatch to
/// <c>catalog.product-unit-conversions.*</c>.
/// </summary>
public sealed class ProductUnitConversionsRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };
    private static readonly string[] Manage = { "catalog.products.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogProductUnitConversion> Config() => new()
    {
        BasePath = "catalog/product-unit-conversions",
        EntityType = CatalogIndexEntity.UnitConversion,
        ResourceKind = "catalog.product_unit_conversion",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = u => u.Id,
        DeletedAtSelector = u => u.DeletedAt,
        TenantIdSelector = u => u.TenantId,
        OrganizationIdSelector = u => u.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogProductUnitConversion>, bool, IOrderedQueryable<CatalogProductUnitConversion>>>
        {
            ["unitCode"] = (q, d) => d ? q.OrderByDescending(u => u.UnitCode) : q.OrderBy(u => u.UnitCode),
            ["sortOrder"] = (q, d) => d ? q.OrderByDescending(u => u.SortOrder) : q.OrderBy(u => u.SortOrder),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(u => u.CreatedAt) : q.OrderBy(u => u.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(u => u.UpdatedAt) : q.OrderBy(u => u.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("productId", out var pid) && Guid.TryParse(pid, out var productId))
                q = q.Where(u => u.ProductId == productId);
            if (query.Filters.TryGetValue("unitCode", out var uc) && !string.IsNullOrWhiteSpace(uc))
                q = q.Where(u => u.UnitCode == uc.Trim());
            if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
                q = q.Where(u => u.IsActive == active);
            return q;
        },
        ProjectItem = u =>
        {
            var item = new Dictionary<string, object?>(CatalogIndexBaseRowResolver.ProjectUnitConversionDoc(u))
            {
                ["unitCode"] = u.UnitCode,             // upstream transformItem emits both keys
                ["metadata"] = CatalogHttp.JsonValue(u.Metadata),
            };
            return item;
        },
        CreatedEvent = "catalog.product_unit_conversion.created",
        UpdatedEvent = "catalog.product_unit_conversion.updated",
        DeletedEvent = "catalog.product_unit_conversion.deleted",
        ValidateCreate = Data.CatalogValidators.UnitConversion,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<UnitConversionCreateInput, UnitConversionResult>(
                "catalog.product-unit-conversions.create",
                new UnitConversionCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.ConversionId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<UnitConversionUpdateInput, UnitConversionResult>(
                "catalog.product-unit-conversions.update", new UnitConversionUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.ConversionId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Record identifier is required");
            var r = await m.Bus.ExecuteWithLog<UnitConversionDeleteInput, UnitConversionResult>(
                "catalog.product-unit-conversions.delete", new UnitConversionDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.ConversionId, r.LogEntry);
        },
    };
}
