using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.RateLimit;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET + POST /api/auth/session/refresh (public). Mirrors upstream api/session/refresh.ts.
/// GET is the browser flow: exchanges the session_token cookie for a fresh auth_token cookie and
/// <b>307</b>-redirects (clearing cookies + redirecting to /login on failure). POST is the API/mobile
/// flow: JSON refreshToken -> 200 {ok,accessToken,expiresIn} / 400 / 401, with cookie side effects.
/// </summary>
public sealed class SessionRefreshRoutes : IAuthRouteGroup
{
    private static readonly AuthRateLimiter.Rule RefreshIpRule =
        AuthRateLimiter.Configure("REFRESH_IP", 60, 60, 60, "refresh-ip");
    private static readonly AuthRateLimiter.Rule RefreshRule =
        AuthRateLimiter.Configure("REFRESH", 15, 60, 60, "refresh");

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/session/refresh", Get);
        routes.MapPost("/api/auth/session/refresh", Post);
    }

    private static async Task<IResult> Get(
        HttpContext http, AppDbContext db, PasswordHasher passwords, TokenHasher tokens,
        EncryptionService enc, JwtService jwt, CancellationToken ct)
    {
        var redirectTo = AuthHttp.SanitizeRedirectPath(
            http.Request.Query["redirect"], AuthHttp.AppBaseUrl(http), "/");
        var origin = AuthHttp.RequestOrigin(http);

        var token = AuthHttp.Cookie(http, "session_token");
        if (token is null)
            return RedirectToLogin(http, origin, redirectTo);

        var auth = new AuthService(db, passwords, tokens, enc);
        var ctx = await auth.RefreshFromSessionTokenAsync(token, ct);
        if (ctx is null)
            return RedirectToLogin(http, origin, redirectTo);

        var (user, roles, session) = ctx.Value;
        var newJwt = jwt.Sign(new StaffJwtClaims
        {
            Sub = user.Id.ToString(),
            Sid = session.Id.ToString(),
            TenantId = user.TenantId?.ToString(),
            OrgId = user.OrganizationId?.ToString(),
            Email = user.Email,
            Roles = roles,
        });
        AuthHttp.SetCookie(http, "auth_token", newJwt, AuthHttp.AccessTokenMaxAgeSeconds);
        return Results.Redirect(origin + redirectTo, permanent: false, preserveMethod: true); // 307
    }

    private static async Task<IResult> Post(
        HttpContext http, AppDbContext db, PasswordHasher passwords, TokenHasher tokens,
        EncryptionService enc, JwtService jwt, CancellationToken ct)
    {
        string? token = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("refreshToken", out var rt)
                && rt.ValueKind == JsonValueKind.String)
            {
                var value = rt.GetString();
                if (!string.IsNullOrEmpty(value)) token = value; // refreshToken min 1
            }
        }
        catch
        {
            // malformed JSON tolerated — token stays null
        }

        var (rateError, _) = await AuthRateLimiter.CheckAsync(http, RefreshIpRule, RefreshRule, token);
        if (rateError is not null) return rateError;

        if (token is null)
        {
            ClearStaffCookies(http);
            return Results.Json(new { ok = false, error = "Missing or invalid refresh token" }, statusCode: 400);
        }

        var auth = new AuthService(db, passwords, tokens, enc);
        var ctx = await auth.RefreshFromSessionTokenAsync(token, ct);
        if (ctx is null)
        {
            ClearStaffCookies(http);
            return Results.Json(new { ok = false, error = "Invalid or expired refresh token" }, statusCode: 401);
        }

        var (user, roles, session) = ctx.Value;
        var newJwt = jwt.Sign(new StaffJwtClaims
        {
            Sub = user.Id.ToString(),
            Sid = session.Id.ToString(),
            TenantId = user.TenantId?.ToString(),
            OrgId = user.OrganizationId?.ToString(),
            Email = user.Email,
            Roles = roles,
        });
        AuthHttp.SetCookie(http, "auth_token", newJwt, AuthHttp.AccessTokenMaxAgeSeconds);
        return Results.Json(new { ok = true, accessToken = newJwt, expiresIn = AuthHttp.AccessTokenMaxAgeSeconds });
    }

    private static IResult RedirectToLogin(HttpContext http, string origin, string redirectTo)
    {
        ClearStaffCookies(http);
        var location = origin + "/login?redirect=" + Uri.EscapeDataString(redirectTo);
        return Results.Redirect(location, permanent: false, preserveMethod: true); // 307
    }

    private static void ClearStaffCookies(HttpContext http)
    {
        AuthHttp.ClearCookie(http, "auth_token", httpOnly: true);
        AuthHttp.ClearCookie(http, "session_token", httpOnly: true);
    }
}
