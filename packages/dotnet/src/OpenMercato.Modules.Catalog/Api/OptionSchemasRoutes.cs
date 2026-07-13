using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Commands;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>
/// Option-schema templates CRUD — the port of upstream <c>api/option-schemas/route.ts</c>. Index-backed
/// list (snake_case + parsed <c>schema</c>/<c>metadata</c>); GET requires <c>catalog.products.view</c>,
/// mutations <c>catalog.settings.manage</c>. Dispatch to <c>catalog.optionSchemas.*</c>.
/// </summary>
public sealed class OptionSchemasRoutes : ICatalogRouteGroup
{
    private static readonly string[] View = { "catalog.products.view" };
    private static readonly string[] Manage = { "catalog.settings.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CatalogOptionSchemaTemplate> Config() => new()
    {
        BasePath = "catalog/option-schemas",
        EntityType = CatalogIndexEntity.OptionSchema,
        ResourceKind = "catalog.option_schema",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = s => s.Id,
        DeletedAtSelector = s => s.DeletedAt,
        TenantIdSelector = s => s.TenantId,
        OrganizationIdSelector = s => s.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CatalogOptionSchemaTemplate>, bool, IOrderedQueryable<CatalogOptionSchemaTemplate>>>
        {
            ["name"] = (q, d) => d ? q.OrderByDescending(s => s.Name) : q.OrderBy(s => s.Name),
            ["code"] = (q, d) => d ? q.OrderByDescending(s => s.Code) : q.OrderBy(s => s.Code),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(s => s.CreatedAt) : q.OrderBy(s => s.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(s => s.UpdatedAt) : q.OrderBy(s => s.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("isActive", out var ia) && CatalogFilter.TryBool(ia, out var active))
                q = q.Where(s => s.IsActive == active);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                q = q.Where(s =>
                    s.Name.ToLower().Contains(term) ||
                    (s.Description != null && s.Description.ToLower().Contains(term)));
            }
            return q;
        },
        ProjectItem = s =>
        {
            var item = new Dictionary<string, object?>(CatalogIndexBaseRowResolver.ProjectOptionSchemaDoc(s))
            {
                ["schema"] = CatalogHttp.JsonValue(s.Schema),
                ["metadata"] = CatalogHttp.JsonValue(s.Metadata),
            };
            return item;
        },
        CreatedEvent = null,
        UpdatedEvent = null,
        DeletedEvent = null,
        ValidateCreate = Data.CatalogValidators.OptionSchema,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<OptionSchemaCreateInput, OptionSchemaResult>(
                "catalog.optionSchemas.create",
                new OptionSchemaCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.SchemaId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CatalogHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<OptionSchemaUpdateInput, OptionSchemaResult>(
                "catalog.optionSchemas.update", new OptionSchemaUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.SchemaId, r.LogEntry, new { ok = true });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = CatalogFilter.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Option schema id is required");
            var r = await m.Bus.ExecuteWithLog<OptionSchemaDeleteInput, OptionSchemaResult>(
                "catalog.optionSchemas.delete", new OptionSchemaDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.SchemaId, r.LogEntry);
        },
    };
}
