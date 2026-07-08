using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Addresses CRUD — the port of upstream <c>api/addresses/route.ts</c> (makeCrudRoute). No soft delete;
/// reuses the <c>customers.activities.*</c> features (upstream feature-mapping quirk). Filters by
/// <c>entityId</c> / <c>id</c>. Writes dispatch to the <c>customers.addresses.*</c> commands.
/// </summary>
public sealed class AddressesRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.activities.view" };
    private static readonly string[] Manage = { "customers.activities.manage" };

    public void Map(IEndpointRouteBuilder routes) => CrudRoute.Map(routes, Config());

    internal static CrudConfig<CustomerAddress> Config() => new()
    {
        BasePath = "customers/addresses",
        EntityType = "customers:customer_address",
        ResourceKind = "customers.address",
        DefaultSortField = "createdAt",
        SoftDelete = false,
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = a => a.Id,
        TenantIdSelector = a => a.TenantId,
        OrganizationIdSelector = a => a.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CustomerAddress>, bool, IOrderedQueryable<CustomerAddress>>>
        {
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(a => a.CreatedAt) : q.OrderBy(a => a.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(a => a.UpdatedAt) : q.OrderBy(a => a.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("entityId", out var eid) && Guid.TryParse(eid, out var e)) q = q.Where(a => a.EntityId == e);
            return q;
        },
        ProjectItem = a => new Dictionary<string, object?>
        {
            ["id"] = a.Id.ToString(), ["entityId"] = a.EntityId.ToString(), ["name"] = a.Name, ["purpose"] = a.Purpose,
            ["companyName"] = a.CompanyName, ["addressLine1"] = a.AddressLine1, ["addressLine2"] = a.AddressLine2,
            ["buildingNumber"] = a.BuildingNumber, ["flatNumber"] = a.FlatNumber, ["city"] = a.City, ["region"] = a.Region,
            ["postalCode"] = a.PostalCode, ["country"] = a.Country, ["latitude"] = a.Latitude, ["longitude"] = a.Longitude,
            ["isPrimary"] = a.IsPrimary, ["organizationId"] = a.OrganizationId.ToString(), ["tenantId"] = a.TenantId.ToString(),
        },
        CreatedEvent = "customers.address.created",
        UpdatedEvent = "customers.address.updated",
        DeletedEvent = "customers.address.deleted",
        ValidateCreate = Data.CustomersValidators.Address,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<AddressCreateInput, AddressResult>(
                "customers.addresses.create", new AddressCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.AddressId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<AddressUpdateInput, AddressResult>(
                "customers.addresses.update", new AddressUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.AddressId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = PeopleRoutes.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Address id is required");
            var r = await m.Bus.ExecuteWithLog<AddressDeleteInput, AddressResult>(
                "customers.addresses.delete", new AddressDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.AddressId, r.LogEntry);
        },
    };
}
