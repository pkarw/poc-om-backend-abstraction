using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET /api/auth/users/consents — 1:1 port of upstream api/users/consents/route.ts (requires
/// <c>auth.users.edit</c>). Returns the user's consent records with per-row integrity verification.
/// </summary>
public sealed class UsersConsentsRouteGroup : IAuthRouteGroup
{
    private const string Iso = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/users/consents", GetAsync).RequireFeatures("auth.users.edit");
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, IRbacService rbac, TenantDataEncryptionService tenc)
    {
        var auth = HttpContextAuth.Current(http)!;
        if (!QueryParse.OptionalGuid(http.Request.Query["userId"], out var userIdOpt) || userIdOpt is null)
            return Results.Json(new { ok = false, error = "Invalid userId" }, statusCode: 400);
        var userId = userIdOpt.Value;
        var tenantId = auth.TenantId;
        var organizationId = auth.OrganizationId;

        try
        {
            await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, tenantId, organizationId, userId);
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }

        var q = db.Set<UserConsent>().AsNoTracking().Where(c => c.UserId == userId && c.DeletedAt == null);
        if (tenantId is { } t) q = q.Where(c => c.TenantId == t);
        if (organizationId is { } o) q = q.Where(c => c.OrganizationId == o);
        var consents = await q.OrderByDescending(c => c.CreatedAt).ToListAsync();

        var items = consents.Select(c =>
        {
            var dec = tenc.DecryptEntityPayload(db, "auth:user_consent", c.TenantId, c.OrganizationId,
                new Dictionary<string, object?> { ["Source"] = c.Source, ["IpAddress"] = c.IpAddress });
            var source = dec["Source"] as string;
            var ip = dec["IpAddress"] as string;
            return (object)new
            {
                id = c.Id.ToString(),
                consentType = c.ConsentType,
                isGranted = c.IsGranted,
                grantedAt = c.GrantedAt?.UtcDateTime.ToString(Iso),
                withdrawnAt = c.WithdrawnAt?.UtcDateTime.ToString(Iso),
                source,
                ipAddress = ip,
                integrityValid = ConsentIntegrity.Verify(c, ip, source, c.IntegrityHash),
                createdAt = c.CreatedAt.UtcDateTime.ToString(Iso),
                updatedAt = c.UpdatedAt?.UtcDateTime.ToString(Iso),
            };
        }).ToList();

        return Results.Json(new { ok = true, items });
    }
}
