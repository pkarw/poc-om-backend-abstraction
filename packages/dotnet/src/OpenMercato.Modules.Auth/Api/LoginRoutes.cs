using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.RateLimit;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// POST /api/auth/login (public). Mirrors upstream api/login.ts: parse form -> two-layer rate limit
/// BEFORE validation -> validate -> lookup by email (exactly-one-match rule across tenants) ->
/// always-run constant-time verify -> optional role gate -> session + JWT -> auth_token / session_token
/// cookies -> 200. Emits auth.login.failed / auth.login.success best-effort.
/// </summary>
public sealed class LoginRoutes : IAuthRouteGroup
{
    private static readonly AuthRateLimiter.Rule LoginIpRule =
        AuthRateLimiter.Configure("LOGIN_IP", 20, 60, 60, "login-ip");
    private static readonly AuthRateLimiter.Rule LoginRule =
        AuthRateLimiter.Configure("LOGIN", 5, 60, 60, "login");

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/login", Handle);
    }

    private static async Task<IResult> Handle(
        HttpContext http,
        AppDbContext db,
        PasswordHasher passwords,
        TokenHasher tokens,
        EncryptionService enc,
        JwtService jwt,
        IEventBus events,
        CancellationToken ct)
    {
        var form = await ReadFormSafe(http);
        var email = form.Get("email");
        var password = form.Get("password");
        var remember = AuthHttp.ParseBooleanToken(form.Get("remember")) == true;
        var tenantIdRaw = FirstNonEmpty(form.Get("tenantId"), form.Get("tenant")).Trim();
        var requireRoleRaw = FirstNonEmpty(form.Get("requireRole"), form.Get("role")).Trim();
        var requiredRoles = requireRoleRaw.Length == 0
            ? Array.Empty<string>()
            : requireRoleRaw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var redirectTo = form.Get("redirect");

        // Two layers, both checked before validation and DB work.
        var (rateError, compoundKey) = await AuthRateLimiter.CheckAsync(http, LoginIpRule, LoginRule, email);
        if (rateError is not null) return rateError;

        // userLoginSchema.pick({ email, password, tenantId }).
        Guid? tenantId = null;
        if (tenantIdRaw.Length > 0)
        {
            if (!Guid.TryParse(tenantIdRaw, out var parsedTenant))
                return Results.Json(new { ok = false, error = "Invalid credentials" }, statusCode: 400);
            tenantId = parsedTenant;
        }
        if (!IsEmail(email) || password.Length < 6)
            return Results.Json(new { ok = false, error = "Invalid credentials" }, statusCode: 400);

        var auth = new AuthService(db, passwords, tokens, enc);

        User? user;
        if (tenantId is Guid tid)
        {
            user = await auth.FindUserByEmailAndTenantAsync(email, tid, ct);
        }
        else
        {
            var users = await auth.FindUsersByEmailAsync(email, ct);
            // Ambiguous multi-tenant email is treated as no user (issue #2242).
            user = users.Count == 1 ? users[0] : null;
        }

        // Always verify — constant-time even when the user is missing.
        var ok = auth.VerifyPassword(user, password);
        if (user is null || !ok)
        {
            var reason = user?.PasswordHash != null ? "invalid_password" : "invalid_credentials";
            await SafePublish(events, "auth.login.failed", new { email, reason });
            return Results.Json(new { ok = false, error = "Invalid email or password" }, statusCode: 401);
        }

        var resolvedTenantId = tenantId ?? user.TenantId;

        if (requiredRoles.Length > 0)
        {
            var roleNames = await auth.GetUserRolesAsync(user, resolvedTenantId, ct);
            var authorized = requiredRoles.Any(r => roleNames.Contains(r));
            if (!authorized)
                return Results.Json(new { ok = false, error = "Not authorized for this area" }, statusCode: 403);
        }

        await auth.UpdateLastLoginAtAsync(user, ct);
        await AuthRateLimiter.ResetAsync(http, compoundKey, LoginRule);
        await SafePublish(events, "query_index.coverage.warmup", new { tenantId = resolvedTenantId });

        var userRoleNames = await auth.GetUserRolesAsync(user, resolvedTenantId, ct);

        var rememberMeDays = ReadInt("REMEMBER_ME_DAYS", 30);
        var now = DateTimeOffset.UtcNow;
        var sessionExpiresAt = remember
            ? now.AddDays(rememberMeDays)
            : now.AddSeconds(AuthHttp.AccessTokenMaxAgeSeconds);
        var (session, refreshToken) = await auth.CreateSessionAsync(user, sessionExpiresAt, ct);

        var token = jwt.Sign(new StaffJwtClaims
        {
            Sub = user.Id.ToString(),
            Sid = session.Id.ToString(),
            TenantId = resolvedTenantId?.ToString(),
            OrgId = user.OrganizationId?.ToString(),
            Email = user.Email,
            Roles = userRoleNames,
        });

        await SafePublish(events, "auth.login.success", new
        {
            id = user.Id.ToString(),
            email = user.Email,
            tenantId = resolvedTenantId?.ToString(),
            organizationId = user.OrganizationId?.ToString(),
        });

        var redirect = AuthHttp.SanitizeRedirectPath(redirectTo, AuthHttp.AppBaseUrl(http), "/backend");

        // Cookies: auth_token always; session_token by remember (persistent) else short-lived.
        AuthHttp.SetCookie(http, "auth_token", token, AuthHttp.AccessTokenMaxAgeSeconds);
        if (remember)
            AuthHttp.SetCookie(http, "session_token", refreshToken, maxAgeSeconds: null, expires: now.AddDays(rememberMeDays));
        else
            AuthHttp.SetCookie(http, "session_token", refreshToken, AuthHttp.AccessTokenMaxAgeSeconds);

        if (remember)
            return Results.Json(new { ok = true, token, redirect, refreshToken });
        return Results.Json(new { ok = true, token, redirect });
    }

    private static async Task SafePublish(IEventBus events, string name, object payload)
    {
        try { await events.PublishAsync(name, payload); } catch { /* fire-and-forget */ }
    }

    private static async Task<FormFields> ReadFormSafe(HttpContext http)
    {
        try { return new FormFields(await http.Request.ReadFormAsync()); }
        catch { return FormFields.Empty; }
    }

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;

    private static bool IsEmail(string value) =>
        value.Length > 0 && System.Net.Mail.MailAddress.TryCreate(value, out _);

    private static int ReadInt(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        return int.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private readonly struct FormFields
    {
        private readonly Microsoft.AspNetCore.Http.IFormCollection? _form;
        public FormFields(Microsoft.AspNetCore.Http.IFormCollection form) => _form = form;
        public static FormFields Empty => new();
        public string Get(string name) => _form is null ? string.Empty : _form[name].ToString();
    }
}
