using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Lib;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// /api/directory/organization-switcher — 1:1 port of upstream api/organization-switcher/route.ts.
/// requireAuth, NO requireFeatures. Returns the accessible-org menu tree plus tenant switching info.
/// The full org-scope resolver + cookie handling are simplified to auth.tenantId/auth.orgId;
/// canViewAllOrganizations mirrors the super-admin/all-orgs upstream quirk.
/// </summary>
public sealed class OrganizationSwitcherRouteGroup : IDirectoryRouteGroup
{
    private static object EmptyBody() => new
    {
        items = Array.Empty<object>(), selectedId = (string?)null, canManage = false,
        canViewAllOrganizations = false, tenantId = (string?)null,
        tenants = Array.Empty<object>(), isSuperAdmin = false,
    };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/directory/organization-switcher", HandleAsync).RequireAuth();
    }

    private static async Task<IResult> HandleAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http);
        if (auth is null) return Results.Json(EmptyBody());

        try
        {
            bool isSuperAdmin;
            bool canManage;
            try
            {
                var acl = await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId);
                isSuperAdmin = acl.IsSuperAdmin;
                canManage = isSuperAdmin
                    || await rbac.UserHasAllFeatures(auth.UserId, new[] { "directory.organizations.manage" }, auth.TenantId, auth.OrganizationId);
            }
            catch { isSuperAdmin = false; canManage = false; }

            // Super-admins may switch tenants (full tenant list); others pinned to auth.tenantId.
            var tenants = isSuperAdmin
                ? (await db.Set<Tenant>().AsNoTracking().Where(t => t.DeletedAt == null).OrderBy(t => t.Name).ToListAsync())
                    .Select(t => (object)new { id = t.Id.ToString(), name = t.Name, isActive = t.IsActive }).ToList()
                : new List<object>();

            var tenantId = auth.TenantId;
            if (tenantId is not { } tid)
                return Results.Json(new { items = Array.Empty<object>(), selectedId = (string?)null, canManage = false, tenantId = (string?)null, tenants, isSuperAdmin });

            var orgs = await db.Set<Organization>().AsNoTracking()
                .Where(o => o.TenantId == tid && o.DeletedAt == null).OrderBy(o => o.Name).ToListAsync();
            var hierarchy = OrganizationHierarchy.Compute(
                orgs.Select(o => new OrgHierarchyInput(o.Id.ToString(), o.ParentId?.ToString(), o.Name, o.IsActive)),
                tid.ToString());

            var rawTenant = http.Request.Query["tenantId"].ToString();
            var selectedId = auth.OrganizationId?.ToString();
            if (string.Equals(rawTenant, "__all__", StringComparison.Ordinal)) selectedId = null;

            object BuildNode(ComputedOrganizationNode n) => new
            {
                id = n.Id,
                name = n.Name,
                depth = n.Depth,
                selectable = true,
                children = n.ChildIds.Where(hierarchy.Map.ContainsKey).Select(cid => BuildNode(hierarchy.Map[cid])).ToList(),
            };
            var items = hierarchy.Ordered.Where(n => n.Depth == 0).Select(BuildNode).ToList();

            // canViewAllOrganizations: super-admins (or all-orgs allowed) can view every org (upstream quirk).
            var canViewAllOrganizations = isSuperAdmin;

            return Results.Json(new
            {
                items, selectedId, canManage, canViewAllOrganizations,
                tenantId = tid.ToString(), tenants, isSuperAdmin,
            });
        }
        catch
        {
            return Results.Json(EmptyBody(), statusCode: 500);
        }
    }
}
