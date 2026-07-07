using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Request-authentication helper. Extracts the staff JWT (Authorization: Bearer, else the
/// <c>auth_token</c> cookie), verifies it, and enforces session binding: the JWT <c>sid</c>
/// must reference a live, non-expired, non-deleted session for the subject (spec 05 R17b).
/// Tokens without a <c>sid</c> are rejected (the legacy grace window is intentionally skipped —
/// see ADR 0006). The resolved <see cref="AuthContext"/> is stashed in HttpContext.Items so
/// handlers read it without re-resolving.
/// </summary>
public static class HttpContextAuth
{
    public const string ItemsKey = "OpenMercato.AuthContext";

    /// <summary>Resolve and validate the staff principal, or null when unauthenticated/invalid.</summary>
    public static async Task<AuthContext?> ResolveAsync(HttpContext http, AppDbContext db, JwtService jwt)
    {
        var token = ExtractToken(http);
        if (token is null) return null;
        if (!jwt.TryVerify(token, out var claims)) return null;
        if (string.IsNullOrEmpty(claims.Sub) || !Guid.TryParse(claims.Sub, out var userId)) return null;

        // Session binding: a sid is required and must reference a live session.
        if (string.IsNullOrEmpty(claims.Sid) || !Guid.TryParse(claims.Sid, out var sid)) return null;
        var now = DateTimeOffset.UtcNow;
        var sessionAlive = await db.Set<Session>().AsNoTracking().AnyAsync(s =>
            s.Id == sid && s.UserId == userId && s.DeletedAt == null && s.ExpiresAt > now);
        if (!sessionAlive) return null;

        var ctx = new AuthContext(
            UserId: userId,
            Sid: sid,
            TenantId: ParseNullableGuid(claims.TenantId),
            OrganizationId: ParseNullableGuid(claims.OrgId),
            Email: claims.Email ?? string.Empty,
            Roles: claims.Roles);
        http.Items[ItemsKey] = ctx;
        return ctx;
    }

    /// <summary>Return the AuthContext already resolved and stashed by a route filter, if any.</summary>
    public static AuthContext? Current(HttpContext http) =>
        http.Items.TryGetValue(ItemsKey, out var value) ? value as AuthContext : null;

    private static string? ExtractToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            if (value.Length > 0) return value;
        }
        if (http.Request.Cookies.TryGetValue("auth_token", out var cookie) && !string.IsNullOrEmpty(cookie))
            return cookie;
        return null;
    }

    private static Guid? ParseNullableGuid(string? value) =>
        !string.IsNullOrEmpty(value) && Guid.TryParse(value, out var g) ? g : null;
}
