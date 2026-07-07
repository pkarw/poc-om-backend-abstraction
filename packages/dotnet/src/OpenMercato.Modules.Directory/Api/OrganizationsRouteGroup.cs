using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Directory.Lib;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// /api/directory/organizations — 1:1 port of upstream api/organizations/route.ts + the
/// commands/organizations.ts CRUD command handlers. Hand-written GET (views: options/tree/manage)
/// plus command-backed POST(201)/PUT(200)/DELETE(200). Custom-field VALUE read/write, query-index
/// projection, and the super-admin all-tenants aggregate are PARITY-TODO (depend on unported infra);
/// scope is resolved from auth.tenantId. Hierarchy is maintained via OrganizationHierarchy.
/// </summary>
public sealed class OrganizationsRouteGroup : IDirectoryRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/directory/organizations", ListAsync).RequireFeatures("directory.organizations.view");
        routes.MapPost("/api/directory/organizations", CreateAsync).RequireFeatures("directory.organizations.manage");
        routes.MapPut("/api/directory/organizations", UpdateAsync).RequireFeatures("directory.organizations.manage");
        routes.MapDelete("/api/directory/organizations", DeleteAsync).RequireFeatures("directory.organizations.manage");
    }

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    private static string[] Arr(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // ---- GET (hand-written; views options / tree / manage) ------------------------------------

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http);
        if (auth is null) return Results.Json(new { items = Array.Empty<object>() });

        var q = http.Request.Query;
        var view = q["view"].ToString();
        if (string.IsNullOrEmpty(view)) view = "options";
        if (view is not ("options" or "manage" or "tree"))
            return Results.Json(new { items = Array.Empty<object>() }, statusCode: 400);

        bool isSuperAdmin;
        try { isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin; }
        catch { isSuperAdmin = false; }

        // Tenant-scope resolution (simplified 1:1 for the common single-tenant path). Super-admin
        // all-tenants aggregate + cookie/org-scope fallback are PARITY-TODO.
        Guid? requestedTenant = null;
        var rawTenant = q["tenantId"].ToString();
        if (!string.IsNullOrEmpty(rawTenant) && !string.Equals(rawTenant, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(rawTenant, out var rt))
                return Results.Json(new { items = Array.Empty<object>(), error = "Tenant scope required" }, statusCode: 400);
            requestedTenant = rt;
        }

        var scopeTenant = isSuperAdmin ? (requestedTenant ?? auth.TenantId) : auth.TenantId;
        if (!isSuperAdmin && requestedTenant is { } req && req != auth.TenantId)
            return Results.Json(new { items = Array.Empty<object>(), error = "Tenant scope required" }, statusCode: 400);
        if (scopeTenant is not { } tenantId)
            return Results.Json(new { items = Array.Empty<object>(), error = "Tenant scope required" }, statusCode: 400);

        var includeInactive = DirectoryRouteHelpers.ParseBooleanToken(q["includeInactive"].ToString());
        var status = q["status"].ToString();
        if (string.IsNullOrEmpty(status)) status = "all";
        var idsFilter = q["ids"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToHashSet();

        var all = await db.Set<Organization>().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.DeletedAt == null)
            .OrderBy(o => o.Name).ToListAsync();

        bool StatusOk(Organization o) =>
            status == "active" ? o.IsActive
            : status == "inactive" ? !o.IsActive
            : includeInactive == false ? o.IsActive
            : true;

        if (view == "options")
        {
            var optionItems = all.Where(StatusOk)
                .Where(o => idsFilter.Count == 0 || idsFilter.Contains(o.Id.ToString()))
                .Select(o => (object)new
                {
                    id = o.Id.ToString(),
                    name = o.Name,
                    logoUrl = o.LogoUrl,
                    parentId = o.ParentId?.ToString(),
                    tenantId = o.TenantId.ToString(),
                    isActive = o.IsActive,
                    depth = o.Depth,
                    treePath = o.TreePath,
                }).ToList();
            return Results.Json(new { items = optionItems });
        }

        // Compute hierarchy for tree/manage.
        var hierarchy = OrganizationHierarchy.Compute(
            all.Select(o => new OrgHierarchyInput(o.Id.ToString(), o.ParentId?.ToString(), o.Name, o.IsActive)),
            tenantId.ToString());

        if (view == "tree")
        {
            var byId = all.ToDictionary(o => o.Id.ToString());
            object BuildNode(ComputedOrganizationNode n)
            {
                return new
                {
                    id = n.Id,
                    name = n.Name,
                    parentId = n.ParentId,
                    tenantId = n.TenantId,
                    depth = n.Depth,
                    ancestorIds = n.AncestorIds,
                    childIds = n.ChildIds,
                    descendantIds = n.DescendantIds,
                    isActive = n.IsActive,
                    treePath = n.TreePath,
                    pathLabel = n.PathLabel,
                    children = n.ChildIds
                        .Where(hierarchy.Map.ContainsKey)
                        .Select(cid => BuildNode(hierarchy.Map[cid])).ToList(),
                };
            }
            var roots = hierarchy.Ordered.Where(n => n.Depth == 0).Select(BuildNode).ToList();
            return Results.Json(new { items = roots });
        }

        // view == manage (single tenant).
        var page = 1; var pageSize = 50;
        DirectoryRouteHelpers.CoerceIntWithDefault(q["page"], 1, 1, int.MaxValue, out page);
        DirectoryRouteHelpers.CoerceIntWithDefault(q["pageSize"], 50, 1, 200, out pageSize);

        var filtered = all.Where(StatusOk)
            .Where(o => idsFilter.Count == 0 || idsFilter.Contains(o.Id.ToString()))
            .ToList();
        var total = filtered.Count;
        var pageRows = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var nameById = all.ToDictionary(o => o.Id.ToString(), o => o.Name);

        var items = pageRows.Select(o =>
        {
            var node = hierarchy.Map.TryGetValue(o.Id.ToString(), out var n) ? n : null;
            var childIds = node?.ChildIds ?? Arr(o.ChildIdsJson);
            var descIds = node?.DescendantIds ?? Arr(o.DescendantIdsJson);
            return (object)new
            {
                id = o.Id.ToString(),
                name = o.Name,
                slug = o.Slug,
                logoUrl = o.LogoUrl,
                updatedAt = o.UpdatedAt == default ? null : Iso(o.UpdatedAt),
                tenantId = o.TenantId.ToString(),
                tenantName = isSuperAdmin ? o.TenantId.ToString() : null,
                parentId = o.ParentId?.ToString(),
                parentName = o.ParentId is { } p && nameById.TryGetValue(p.ToString(), out var pn) ? pn : null,
                depth = node?.Depth ?? o.Depth,
                rootId = node?.RootId ?? o.RootId?.ToString(),
                treePath = node?.TreePath ?? o.TreePath,
                pathLabel = node?.PathLabel ?? o.Name,
                ancestorIds = node?.AncestorIds ?? Arr(o.AncestorIdsJson),
                childIds,
                descendantIds = descIds,
                childrenCount = childIds.Count,
                descendantsCount = descIds.Count,
                isActive = o.IsActive,
                // ...cf_* — PARITY-TODO custom-field values.
            };
        }).ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return Results.Json(new { items, total, page, pageSize, totalPages, isSuperAdmin });
    }

    // ---- POST / PUT / DELETE (command semantics inlined) --------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await DirectoryRouteHelpers.ReadJsonAsync(http);
        try
        {
            if (!body.TryGetString("name", out var name) || name.Length < 1 || name.Length > 200)
                return Results.Json(new { error = "Invalid payload" }, statusCode: 400);

            var tenantProvided = body.HasProperty("tenantId") && !body.IsNullProperty("tenantId");
            Guid? requested = null;
            if (tenantProvided)
            {
                if (!body.TryGetString("tenantId", out var ts) || !Guid.TryParse(ts, out var tg))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                requested = tg;
            }
            var isSuperAdmin = await TenantAccess.ResolveIsSuperAdmin(rbac, auth);
            var tenantId = TenantAccess.EnforceTenantSelection(isSuperAdmin, auth.TenantId, tenantProvided, requested);
            if (tenantId is not { } tid)
                return Results.Json(new { error = "Tenant scope required" }, statusCode: 400);

            var isActive = body.TryGetBool("isActive") ?? true;

            Guid? parentId = null;
            if (body.HasProperty("parentId") && !body.IsNullProperty("parentId"))
            {
                if (!body.TryGetString("parentId", out var ps) || !Guid.TryParse(ps, out var pg))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                var parentExists = await db.Set<Organization>().AnyAsync(o => o.Id == pg && o.TenantId == tid && o.DeletedAt == null);
                if (!parentExists) return Results.Json(new { error = "Parent not found" }, statusCode: 400);
                parentId = pg;
            }

            var childIds = (body.TryGetStringArray("childIds") ?? Array.Empty<string>())
                .Select(x => Guid.TryParse(x, out var g) ? (Guid?)g : null).Where(g => g is not null).Select(g => g!.Value).ToList();

            // slug
            string? slug = null;
            if (body.HasProperty("slug") && !body.IsNullProperty("slug"))
            {
                if (!body.TryGetString("slug", out var rawSlug) || !DirectoryRouteHelpers.TryNormalizeSlug(rawSlug, out slug))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            }
            slug ??= Slugify.Run(name);
            if (string.IsNullOrEmpty(slug)) slug = null;

            string? logoUrl = null;
            if (body.HasProperty("logoUrl") && !body.IsNullProperty("logoUrl"))
            {
                if (!body.TryGetString("logoUrl", out var raw) || !DirectoryRouteHelpers.IsValidLogoUrl(raw))
                    return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                logoUrl = raw.Trim();
            }

            var orgId = Guid.NewGuid();
            if (childIds.Contains(orgId)) return Results.Json(new { error = "Child cannot equal parent" }, statusCode: 400);
            if (childIds.Count > 0)
            {
                var existingChildren = await db.Set<Organization>()
                    .CountAsync(o => childIds.Contains(o.Id) && o.TenantId == tid && o.DeletedAt == null);
                if (existingChildren != childIds.Distinct().Count())
                    return Results.Json(new { error = "Invalid child assignment" }, statusCode: 400);
            }

            if (slug is not null)
                slug = await OrganizationHierarchy.ResolveUniqueSlugAsync(db, tid, slug);

            var now = DateTimeOffset.UtcNow;
            var org = new Organization
            {
                Id = orgId, TenantId = tid, Name = name, Slug = slug, LogoUrl = logoUrl,
                IsActive = isActive, ParentId = parentId, RootId = orgId, TreePath = orgId.ToString(),
                CreatedAt = now, UpdatedAt = now,
            };
            db.Set<Organization>().Add(org);
            // assignChildren: re-parent selected children to the new org.
            if (childIds.Count > 0)
            {
                var children = await db.Set<Organization>().Where(o => childIds.Contains(o.Id) && o.TenantId == tid).ToListAsync();
                foreach (var c in children) c.ParentId = orgId;
            }
            await db.SaveChangesAsync();
            await OrganizationHierarchy.RebuildForTenantAsync(db, tid);

            await DirectoryRouteHelpers.EmitAsync(http, "directory.organization.created",
                new { id = orgId.ToString(), tenantId = tid.ToString(), organizationId = orgId.ToString() });
            return Results.Json(new { id = orgId.ToString() }, statusCode: 201);
        }
        catch (AuthHttpException ex) { return Results.Json(ex.Body, statusCode: ex.Status); }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await DirectoryRouteHelpers.ReadJsonAsync(http);
        if (!body.TryGetString("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
        try
        {
            var org = await db.Set<Organization>().FirstOrDefaultAsync(o => o.Id == id && o.DeletedAt == null);
            if (org is null) return Results.Json(new { error = "Not found" }, statusCode: 404);
            var tid = org.TenantId;

            if (body.TryGetString("name", out var name))
            {
                if (name.Length < 1 || name.Length > 200) return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                org.Name = name;
            }
            if (body.HasProperty("slug"))
            {
                if (body.IsNullProperty("slug")) org.Slug = null;
                else if (body.TryGetString("slug", out var rawSlug) && DirectoryRouteHelpers.TryNormalizeSlug(rawSlug, out var s))
                    org.Slug = await OrganizationHierarchy.ResolveUniqueSlugAsync(db, tid, s, id);
                else return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            }
            if (body.HasProperty("logoUrl"))
            {
                if (body.IsNullProperty("logoUrl")) org.LogoUrl = null;
                else if (body.TryGetString("logoUrl", out var raw) && DirectoryRouteHelpers.IsValidLogoUrl(raw)) org.LogoUrl = raw.Trim();
                else return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            }
            if (body.TryGetBool("isActive") is { } ia) org.IsActive = ia;

            // parentId: when the key is provided, always set (incl. null), after validation.
            if (body.HasProperty("parentId"))
            {
                Guid? parentId = null;
                if (!body.IsNullProperty("parentId"))
                {
                    if (!body.TryGetString("parentId", out var ps) || !Guid.TryParse(ps, out var pg))
                        return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
                    if (pg == id) return Results.Json(new { error = "Organization cannot be its own parent" }, statusCode: 400);
                    var parent = await db.Set<Organization>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == pg && o.TenantId == tid && o.DeletedAt == null);
                    if (parent is null) return Results.Json(new { error = "Parent not found" }, statusCode: 400);
                    if (Arr(org.DescendantIdsJson).Contains(pg.ToString()))
                        return Results.Json(new { error = "Cannot assign descendant as parent" }, statusCode: 400);
                    parentId = pg;
                }
                org.ParentId = parentId;
            }

            org.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await OrganizationHierarchy.RebuildForTenantAsync(db, tid);

            await DirectoryRouteHelpers.EmitAsync(http, "directory.organization.updated",
                new { id = id.ToString(), tenantId = tid.ToString(), organizationId = id.ToString() });
            return Results.Json(new { ok = true });
        }
        catch (AuthHttpException ex) { return Results.Json(ex.Body, statusCode: ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, IRbacService rbac)
    {
        _ = HttpContextAuth.Current(http)!;
        var idStr = http.Request.Query["id"].ToString();
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Organization id required" }, statusCode: 400);

        var org = await db.Set<Organization>().FirstOrDefaultAsync(o => o.Id == id && o.DeletedAt == null);
        if (org is null) return Results.Json(new { error = "Not found" }, statusCode: 404);
        var tid = org.TenantId;
        var originalParent = org.ParentId;

        // Re-parent children (promote to grandparent) then soft-delete.
        var children = await db.Set<Organization>().Where(o => o.ParentId == id && o.DeletedAt == null).ToListAsync();
        foreach (var c in children) c.ParentId = originalParent;

        org.DeletedAt = DateTimeOffset.UtcNow;
        org.IsActive = false;
        org.ParentId = null;
        await db.SaveChangesAsync();
        await OrganizationHierarchy.RebuildForTenantAsync(db, tid);

        await DirectoryRouteHelpers.EmitAsync(http, "directory.organization.deleted",
            new { id = id.ToString(), tenantId = tid.ToString(), organizationId = id.ToString() });
        return Results.Json(new { ok = true });
    }
}
