using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.RateLimit;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;
using OpenMercato.Modules.Auth.Validators;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// POST /api/auth/reset + POST /api/auth/reset/confirm (public). Mirrors upstream api/reset.ts and
/// api/reset/confirm.ts. Reset request is existence-hiding (always 200 {ok:true}). Confirm is the
/// atomic compare-and-set token consume that also revokes all of the user's sessions.
///
/// PARITY-TODO: the reset email (ResetPasswordEmail) and the tenant-scoped password-reset
/// notifications are not sent here — notifications/email modules are not ported. The token row is
/// still created so /reset/confirm works. Email-origin config errors (400/500) do not occur.
/// </summary>
public sealed class ResetRoutes : IAuthRouteGroup
{
    private static readonly AuthRateLimiter.Rule ResetIpRule =
        AuthRateLimiter.Configure("RESET_IP", 10, 60, 60, "reset-ip");
    private static readonly AuthRateLimiter.Rule ResetRule =
        AuthRateLimiter.Configure("RESET", 3, 60, 60, "reset");
    private static readonly AuthRateLimiter.Rule ResetConfirmRule =
        AuthRateLimiter.Configure("RESET_CONFIRM", 5, 300, 0, "reset-confirm");

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/reset", Request);
        routes.MapPost("/api/auth/reset/confirm", Confirm);
    }

    private static async Task<IResult> Request(
        HttpContext http, AppDbContext db, PasswordHasher passwords, TokenHasher tokens,
        EncryptionService enc, TenantDataEncryptionService tenc, CancellationToken ct)
    {
        var email = (await ReadFormValue(http, "email")).Trim();

        var (rateError, _) = await AuthRateLimiter.CheckAsync(http, ResetIpRule, ResetRule, email);
        if (rateError is not null) return rateError;

        if (!IsEmail(email))
            return Results.Json(new { error = "Validation failed", fieldErrors = new { email = new[] { "Invalid email" } } }, statusCode: 422);

        var auth = new AuthService(db, passwords, tokens, enc, tenc);
        await auth.RequestPasswordResetAsync(email, ct); // token row created for known users
        return Results.Json(new { ok = true });
    }

    private static async Task<IResult> Confirm(
        HttpContext http, AppDbContext db, PasswordHasher passwords, TokenHasher tokens,
        EncryptionService enc, TenantDataEncryptionService tenc, CancellationToken ct)
    {
        var form = await ReadForm(http);
        var token = form.TryGetValue("token", out var t) ? t : string.Empty;
        var password = form.TryGetValue("password", out var p) ? p : string.Empty;

        var (rateError, _) = await AuthRateLimiter.CheckAsync(http, ResetConfirmRule);
        if (rateError is not null) return rateError;

        // confirmPasswordResetSchema: token min 10 + password policy.
        if (token.Length < 10 || !PasswordPolicy.IsValid(password))
            return Results.Json(new { ok = false, error = "Invalid request" }, statusCode: 400);

        var auth = new AuthService(db, passwords, tokens, enc, tenc);
        var user = await auth.ConfirmPasswordResetAsync(token, password, ct);
        if (user is null)
            return Results.Json(new { ok = false, error = "Invalid or expired token" }, statusCode: 400);

        // PARITY-TODO: auth.password_reset.completed notification (notifications module not ported).
        return Results.Json(new { ok = true, redirect = "/login" });
    }

    private static async Task<string> ReadFormValue(HttpContext http, string name)
    {
        var form = await ReadForm(http);
        return form.TryGetValue(name, out var v) ? v : string.Empty;
    }

    private static async Task<Dictionary<string, string>> ReadForm(HttpContext http)
    {
        try
        {
            var form = await http.Request.ReadFormAsync();
            return form.Keys.ToDictionary(k => k, k => form[k].ToString());
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static bool IsEmail(string value) =>
        value.Length > 0 && System.Net.Mail.MailAddress.TryCreate(value, out _);
}
