using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// POST /api/auth/logout (auth required). Mirrors upstream api/logout.ts: best-effort delete of the
/// session (by JWT sid and/or by the session_token cookie), then a <b>307</b> redirect to
/// &lt;origin&gt;/login while clearing both auth cookies. No JSON body.
/// </summary>
public sealed class LogoutRoutes : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/logout", Handle).RequireAuth();
    }

    private static async Task<IResult> Handle(
        HttpContext http,
        AppDbContext db,
        PasswordHasher passwords,
        TokenHasher tokens,
        EncryptionService enc,
        TenantDataEncryptionService tenc,
        CancellationToken ct)
    {
        var sessionToken = AuthHttp.Cookie(http, "session_token");
        var sid = HttpContextAuth.Current(http)?.Sid;
        try
        {
            var auth = new AuthService(db, passwords, tokens, enc, tenc);
            if (sid is Guid s) await auth.DeleteSessionByIdAsync(s, ct);
            if (sessionToken is not null) await auth.DeleteSessionByTokenAsync(sessionToken, ct);
        }
        catch
        {
            // upstream swallows session-deletion failures
        }

        AuthHttp.ClearCookie(http, "auth_token");
        AuthHttp.ClearCookie(http, "session_token");
        var location = AuthHttp.RequestOrigin(http) + "/login";
        return Results.Redirect(location, permanent: false, preserveMethod: true); // 307
    }
}
