using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// <c>GET /api/customers/assignable-staff</c> — the port of upstream <c>api/assignable-staff/route.ts</c>:
/// a DEPRECATED 308 permanent redirect to <c>/api/staff/team-members/assignable</c>, preserving the
/// query string. No body logic. Requires auth + <c>customers.roles.view</c>.
/// </summary>
public sealed class AssignableStaffRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.roles.view" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/assignable-staff", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (_, denied) = await CustomersHttp.AuthorizeAsync(http, View);
            if (denied is not null) return denied;
            var target = "/api/staff/team-members/assignable";
            var qs = http.Request.QueryString.Value;
            if (!string.IsNullOrEmpty(qs)) target += qs;
            return Results.Redirect(target, permanent: true, preserveMethod: true);
        }));
    }
}
