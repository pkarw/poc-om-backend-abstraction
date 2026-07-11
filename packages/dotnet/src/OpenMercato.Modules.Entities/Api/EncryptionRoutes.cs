using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>GET /api/entities/encryption</c> — the per-entity encryption-map config read (upstream
/// api/encryption.ts GET). Returns which fields of an entity are encrypted at rest, resolving the
/// <c>encryption_maps</c> row by scope precedence (tenant+org → tenant → global). Requires
/// <c>entities.definitions.manage</c>; powers the <c>config/encryption</c> admin page.
///
/// PARITY: read-only. The POST (edit) path and the actual field-value encryption RUNTIME (SaveChanges
/// interceptor + tenant DEKs) are unported — maps are declarative here (see RecordCustomFields PARITY-TODO).
/// The declared maps are seeded into <c>encryption_maps</c> per tenant (InitialTenantSeeder), so this
/// returns real data on a Postgres-backed tenant and an empty map otherwise.
/// </summary>
public static class EncryptionRoutes
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/encryption", (Func<HttpContext, Task<IResult>>)GetAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;
        if (ctx!.TenantId is null) return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var entityId = http.Request.Query["entityId"].ToString();
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { error = "entityId is required" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var candidates = await db.Set<EncryptionMap>().AsNoTracking()
            .Where(m => m.EntityId == entityId && m.DeletedAt == null
                        && (m.TenantId == null || m.TenantId == ctx.TenantId)
                        && (m.OrganizationId == null || m.OrganizationId == ctx.OrganizationId))
            .ToListAsync();

        // Scope precedence: tenant+org > tenant-only > global.
        var map = candidates
            .OrderByDescending(m => (m.TenantId is not null ? 1 : 0) + (m.OrganizationId is not null ? 2 : 0))
            .FirstOrDefault();

        object fields = Array.Empty<object>();
        if (map?.FieldsJson is { Length: > 0 })
        {
            try { fields = JsonSerializer.Deserialize<JsonElement>(map.FieldsJson); }
            catch { fields = Array.Empty<object>(); }
        }

        return EntitiesHttp.Result(new
        {
            entityId,
            tenantId = map?.TenantId?.ToString() ?? ctx.TenantId?.ToString(),
            organizationId = map?.OrganizationId?.ToString() ?? ctx.OrganizationId?.ToString(),
            fields,
            isActive = map?.IsActive ?? true,
            updatedAt = map is null ? null : map.UpdatedAt.ToUniversalTime().ToString("o"),
        }, 200);
    }
}
