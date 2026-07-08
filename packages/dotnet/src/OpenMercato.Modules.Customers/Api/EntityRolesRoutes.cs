using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Entity-roles factory — the port of upstream <c>entity-roles-factory.ts</c>. Both
/// <c>people/[id]/roles</c> and <c>companies/[id]/roles</c> are thin wrappers over this shared handler
/// set (<c>createEntityRolesHandlers('person'|'company')</c>). GET requires <c>customers.roles.view</c>,
/// writes <c>customers.roles.manage</c>, and every method re-checks the feature on the target entity's
/// org (403 Access denied).
/// </summary>
internal static class EntityRolesRoutes
{
    private const string View = "customers.roles.view";
    private const string Manage = "customers.roles.manage";

    public static void Map(IEndpointRouteBuilder routes, string kind, string path)
    {
        routes.MapGet(path, (Func<HttpContext, string, Task<IResult>>)((http, id) => ListAsync(http, id, kind)));
        routes.MapPost(path, (Func<HttpContext, string, Task<IResult>>)((http, id) => CreateAsync(http, id, kind)));
        routes.MapPut(path, (Func<HttpContext, string, Task<IResult>>)((http, id) => UpdateAsync(http, id, kind)));
        routes.MapDelete(path, (Func<HttpContext, string, Task<IResult>>)((http, id) => DeleteAsync(http, id, kind)));
    }

    private static async Task<IResult> ListAsync(HttpContext http, string id, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, new[] { View });
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var entityId)) return CustomersHttp.Json(new { error = "Customer not found" }, 404);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == entityId && e.Kind == kind && e.DeletedAt == null && e.TenantId == ctx!.TenantId);
        if (entity is null) return CustomersHttp.Json(new { error = "Customer not found" }, 404);
        if (!await CustomersHttp.HasFeatureAsync(http, ctx!, View)) return CustomersHttp.Json(new { error = "Access denied" }, 403);

        var roles = await db.Set<CustomerEntityRole>().AsNoTracking()
            .Where(r => r.EntityType == kind && r.EntityId == entityId && r.DeletedAt == null)
            .OrderBy(r => r.RoleType).ToListAsync();
        var items = roles.Select(r => new
        {
            userName = (string?)null, userEmail = (string?)null, userPhone = (string?)null,
            id = r.Id.ToString(), entityType = r.EntityType, entityId = r.EntityId.ToString(),
            userId = r.UserId.ToString(), roleType = r.RoleType,
            createdAt = CustomersHttp.Iso(r.CreatedAt), updatedAt = CustomersHttp.Iso(r.UpdatedAt),
        }).ToList();
        return CustomersHttp.Json(new { items }, 200);
        // PARITY-TODO: userName/userEmail hydration via the auth User join.
    }

    private static async Task<IResult> CreateAsync(HttpContext http, string id, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, new[] { Manage });
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var entityId)) return CustomersHttp.Json(new { error = "Customer not found" }, 404);
        if (!await CustomersHttp.HasFeatureAsync(http, ctx!, Manage)) return CustomersHttp.Json(new { error = "Access denied" }, 403);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var roleType = CustomersHttp.Str(body, "roleType")?.Trim();
        var userId = CustomersHttp.GuidOf(body, "userId");
        if (string.IsNullOrEmpty(roleType) || roleType.Length > 100 || userId is null)
            return CustomersHttp.Json(new { error = "Invalid input" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<EntityRoleCreateInput, EntityRoleResult>(
                "customers.entityRoles.create",
                new EntityRoleCreateInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, kind, entityId, roleType, userId.Value), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { id = r.Result.RoleId }, 201);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, string id, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, new[] { Manage });
        if (denied is not null) return denied;
        if (!await CustomersHttp.HasFeatureAsync(http, ctx!, Manage)) return CustomersHttp.Json(new { error = "Access denied" }, 403);
        var roleId = http.Request.Query["roleId"].ToString();
        if (!Guid.TryParse(roleId, out var rid)) return CustomersHttp.Json(new { error = "Role not found" }, 404);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var userId = CustomersHttp.GuidOf(body, "userId");
        if (userId is null) return CustomersHttp.Json(new { error = "Invalid input" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<EntityRoleUpdateInput, EntityRoleResult>(
                "customers.entityRoles.update",
                new EntityRoleUpdateInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, rid, userId.Value), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, string id, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, new[] { Manage });
        if (denied is not null) return denied;
        if (!await CustomersHttp.HasFeatureAsync(http, ctx!, Manage)) return CustomersHttp.Json(new { error = "Access denied" }, 403);
        var roleId = http.Request.Query["roleId"].ToString();
        if (!Guid.TryParse(roleId, out var rid)) return CustomersHttp.Json(new { error = "Role not found" }, 404);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<EntityRoleDeleteInput, EntityRoleResult>(
                "customers.entityRoles.delete",
                new EntityRoleDeleteInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, rid), ctx);
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }
}
