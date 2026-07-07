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
/// POST /api/auth/users/resend-invite — 1:1 port of upstream api/users/resend-invite/route.ts
/// (requires <c>auth.users.create</c>). Invalidates open reset rows and issues a new 48h invite token.
///
/// The in-handler IP rate limit (resend-invite 3/300+300) and email delivery depend on unported infra
/// and are best-effort no-ops here (// PARITY-TODO); the token lifecycle is faithful.
/// </summary>
public sealed class UsersResendInviteRouteGroup : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/users/resend-invite", PostAsync).RequireFeatures("auth.users.create");
    }

    private static async Task<IResult> PostAsync(HttpContext http, AppDbContext db, IRbacService rbac, TokenHasher tokens)
    {
        var auth = HttpContextAuth.Current(http)!;
        // // PARITY-TODO: in-handler IP rate limit (fail-open) not ported.

        var body = await AuthRouteHelpers.ReadJsonObjectAsync(http);
        if (!body.TryGetString("id", out var idStr) || !Guid.TryParse(idStr, out var id))
            return Results.Json(new { error = "Validation failed", fieldErrors = new { id = new[] { "Invalid uuid" } } }, statusCode: 422);

        bool isSuperAdmin;
        try { isSuperAdmin = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).IsSuperAdmin; }
        catch { isSuperAdmin = false; }

        try
        {
            await GrantChecks.AssertActorCanAccessUserTarget(db, rbac, auth.UserId, auth.TenantId, auth.OrganizationId, id, isSuperAdmin);
        }
        catch (AuthHttpException ex)
        {
            return Results.Json(ex.Body, statusCode: ex.Status);
        }

        var query = db.Set<User>().Where(u => u.Id == id && u.DeletedAt == null);
        if (!isSuperAdmin)
        {
            if (auth.TenantId is null) return Results.Json(new { error = "User not found" }, statusCode: 404);
            query = query.Where(u => u.TenantId == auth.TenantId);
        }
        var user = await query.FirstOrDefaultAsync();
        if (user is null) return Results.Json(new { error = "User not found" }, statusCode: 404);
        if (!string.IsNullOrEmpty(user.PasswordHash))
            return Results.Json(new { error = "User already has a password" }, statusCode: 409);

        // Invalidate all open reset/invite rows, then issue a fresh 48h invite token.
        var open = await db.Set<PasswordReset>().Where(p => p.UserId == user.Id && p.UsedAt == null).ToListAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in open) row.UsedAt = now;

        var raw = tokens.Generate();
        db.Set<PasswordReset>().Add(new PasswordReset
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = tokens.Hash(raw),
            ExpiresAt = now.AddHours(48),
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // Email delivery is out of scope (no mailer) — always report success. // PARITY-TODO
        return Results.Json(new { ok = true });
    }
}
