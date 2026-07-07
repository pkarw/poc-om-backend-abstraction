using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Directory.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Directory.Api;

/// <summary>
/// /api/directory/tenants — 1:1 port of upstream api/tenants/route.ts. Hand-written GET (empty
/// envelope on unauth) plus command-backed POST(201)/PUT(200)/DELETE(200). Custom-field VALUE
/// read/write is a PARITY-TODO no-op (depends on the unported shared DataEngine/custom-fields).
/// </summary>
public sealed class TenantsRouteGroup : IDirectoryRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/directory/tenants", ListAsync).RequireFeatures("directory.tenants.view");
        routes.MapPost("/api/directory/tenants", CreateAsync).RequireFeatures("directory.tenants.manage");
        routes.MapPut("/api/directory/tenants", UpdateAsync).RequireFeatures("directory.tenants.manage");
        routes.MapDelete("/api/directory/tenants", DeleteAsync).RequireFeatures("directory.tenants.manage");
    }

    private static string Iso(DateTimeOffset? d) => d?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? "";

    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http);
        if (auth is null)
            return Results.Json(new { items = Array.Empty<object>(), total = 0, page = 1, pageSize = 50, totalPages = 1 });

        var q = http.Request.Query;
        Guid? id = null;
        if (!string.IsNullOrEmpty(q["id"].ToString()))
        {
            if (!Guid.TryParse(q["id"].ToString(), out var g))
                return Results.Json(new { error = "Invalid query parameters", details = new { } }, statusCode: 400);
            id = g;
        }
        if (!DirectoryRouteHelpers.CoerceIntWithDefault(q["page"], 1, 1, int.MaxValue, out var page)
            || !DirectoryRouteHelpers.CoerceIntWithDefault(q["pageSize"], 50, 1, 100, out var pageSize))
            return Results.Json(new { error = "Invalid query parameters", details = new { } }, statusCode: 400);

        var search = q["search"].ToString();
        var isActiveToken = DirectoryRouteHelpers.ParseBooleanToken(q["isActive"].ToString());
        var sortField = q["sortField"].ToString();
        var sortDir = q["sortDir"].ToString().ToLowerInvariant();

        var rows = db.Set<Tenant>().AsNoTracking().Where(t => t.DeletedAt == null);
        if (id is { } tid) rows = rows.Where(t => t.Id == tid);
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").ToLowerInvariant()}%";
            rows = rows.Where(t => EF.Functions.Like(t.Name.ToLower(), pattern, "\\"));
        }
        if (isActiveToken is { } ia) rows = rows.Where(t => t.IsActive == ia);

        rows = (sortField, sortDir) switch
        {
            ("createdAt", "desc") => rows.OrderByDescending(t => t.CreatedAt),
            ("createdAt", _) => rows.OrderBy(t => t.CreatedAt),
            ("updatedAt", "desc") => rows.OrderByDescending(t => t.UpdatedAt),
            ("updatedAt", _) => rows.OrderBy(t => t.UpdatedAt),
            ("name", "desc") => rows.OrderByDescending(t => t.Name),
            _ => rows.OrderBy(t => t.Name),
        };

        var total = await rows.CountAsync();
        var pageRows = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var items = pageRows.Select(t => (object)new
        {
            id = t.Id.ToString(),
            name = t.Name,
            isActive = t.IsActive,
            createdAt = t.CreatedAt == default ? null : Iso(t.CreatedAt),
            updatedAt = t.UpdatedAt == default ? null : Iso(t.UpdatedAt),
            // ...customFields — PARITY-TODO: merge cf_* values via the shared DataEngine.
        }).ToList();

        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return Results.Json(new { items, total, page, pageSize, totalPages });
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AppDbContext db)
    {
        var auth = HttpContextAuth.Current(http)!;
        var body = await DirectoryRouteHelpers.ReadJsonAsync(http);
        if (!body.TryGetString("name", out var name) || name.Length < 1 || name.Length > 200)
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
        var isActive = body.TryGetBool("isActive") ?? true;

        var now = DateTimeOffset.UtcNow;
        var tenant = new Tenant { Id = Guid.NewGuid(), Name = name, IsActive = isActive, CreatedAt = now, UpdatedAt = now };
        db.Set<Tenant>().Add(tenant);
        await db.SaveChangesAsync();

        // Identifiers use the tenant's OWN id as tenantId.
        await DirectoryRouteHelpers.EmitAsync(http, "directory.tenant.created",
            new { id = tenant.Id.ToString(), organizationId = (string?)null, tenantId = tenant.Id.ToString() });
        // PARITY-TODO: tenant DEK provisioning (kmsService), custom-field values.
        return Results.Json(new { id = tenant.Id.ToString() }, statusCode: 201);
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, AppDbContext db)
    {
        _ = HttpContextAuth.Current(http)!;
        var body = await DirectoryRouteHelpers.ReadJsonAsync(http);
        if (!body.TryGetString("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Invalid payload" }, statusCode: 400);

        var tenant = await db.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null);
        if (tenant is null) return Results.Json(new { error = "Tenant not found" }, statusCode: 404);

        if (body.TryGetString("name", out var name))
        {
            if (name.Length < 1 || name.Length > 200) return Results.Json(new { error = "Invalid payload" }, statusCode: 400);
            tenant.Name = name;
        }
        if (body.TryGetBool("isActive") is { } ia) tenant.IsActive = ia;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await DirectoryRouteHelpers.EmitAsync(http, "directory.tenant.updated",
            new { id = tenant.Id.ToString(), organizationId = (string?)null, tenantId = tenant.Id.ToString() });
        return Results.Json(new { ok = true });
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db)
    {
        _ = HttpContextAuth.Current(http)!;
        var idStr = http.Request.Query["id"].ToString();
        if (string.IsNullOrEmpty(idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Tenant id required" }, statusCode: 400);

        var tenant = await db.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == id && t.DeletedAt == null);
        if (tenant is null) return Results.Json(new { error = "Tenant not found" }, statusCode: 404);

        tenant.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await DirectoryRouteHelpers.EmitAsync(http, "directory.tenant.deleted",
            new { id = tenant.Id.ToString(), organizationId = (string?)null, tenantId = tenant.Id.ToString() });
        return Results.Json(new { ok = true });
    }
}
