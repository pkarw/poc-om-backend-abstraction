using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;
using OpenMercato.Modules.Auth.Validators;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET + PUT /api/auth/profile (auth required). Mirrors upstream api/profile/route.ts. GET returns
/// the signed-in user's email + roles (from the JWT). PUT updates own email and/or password:
/// a password change requires the correct currentPassword; on success the JWT is re-signed
/// preserving the existing <c>sid</c> and re-set as the auth_token cookie.
///
/// PARITY-TODO: upstream routes the mutation through the commandBus 'auth.users.update' (per-tenant
/// duplicate-email enforcement, CRUD events, custom fields). Those live in other slices, so this
/// applies the change directly; a DB-level failure maps to the catch-all 400.
/// </summary>
public sealed class ProfileRoutes : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/profile", Get).RequireAuth();
        routes.MapPut("/api/auth/profile", Put).RequireAuth();
    }

    private static async Task<IResult> Get(HttpContext http, AppDbContext db, EncryptionService enc, CancellationToken ct)
    {
        var auth = HttpContextAuth.Current(http)!;
        try
        {
            var user = await db.Set<User>().AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == auth.UserId && u.DeletedAt == null, ct);
            if (user is null)
                return Results.Json(new { error = "User not found" }, statusCode: 404);
            var email = enc.Decrypt(user.Email) ?? user.Email;
            return Results.Json(new { email, roles = auth.Roles });
        }
        catch
        {
            return Results.Json(new { error = "Failed to load profile." }, statusCode: 400);
        }
    }

    private static async Task<IResult> Put(
        HttpContext http, AppDbContext db, PasswordHasher passwords, TokenHasher tokens,
        EncryptionService enc, JwtService jwt, CancellationToken ct)
    {
        var auth = HttpContextAuth.Current(http)!;
        try
        {
            var root = await ReadJsonObject(http, ct);

            string? emailField = GetString(root, "email");
            string? currentField = GetString(root, "currentPassword");
            string? passwordField = GetString(root, "password");

            var issues = new List<object>();
            var emailValid = emailField is not null && IsEmail(emailField);
            var passwordValid = passwordField is not null && PasswordPolicy.IsValid(passwordField);

            if (emailField is not null && !emailValid)
                issues.Add(new { path = new[] { "email" }, message = "Invalid email" });
            if (passwordField is not null && !passwordValid)
                issues.Add(new { path = new[] { "password" }, message = PasswordPolicy.Message });
            if (currentField is not null && currentField.Trim().Length < 1)
                issues.Add(new { path = new[] { "currentPassword" }, message = "String must contain at least 1 character(s)" });

            var effectiveEmail = emailValid ? emailField : null;
            var effectivePassword = passwordValid ? passwordField : null;
            var hasCurrent = currentField is not null && currentField.Trim().Length >= 1;

            if (effectiveEmail is null && effectivePassword is null)
                issues.Add(new { path = new[] { "email" }, message = "Provide an email or password." });
            if (effectivePassword is not null && !hasCurrent)
                issues.Add(new { path = new[] { "currentPassword" }, message = "Current password is required." });
            if (hasCurrent && effectivePassword is null)
                issues.Add(new { path = new[] { "password" }, message = "New password is required." });

            if (issues.Count > 0)
                return Results.Json(new { error = "Invalid profile update.", issues }, statusCode: 400);

            var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == auth.UserId && u.DeletedAt == null, ct);
            if (user is null)
                return Results.Json(new { error = "User not found" }, statusCode: 404);

            if (effectivePassword is not null)
            {
                var isValid = passwords.Verify(user, currentField!.Trim());
                if (!isValid)
                {
                    const string message = "Current password is incorrect.";
                    return Results.Json(new
                    {
                        error = message,
                        issues = new[] { new { path = new[] { "currentPassword" }, message } },
                    }, statusCode: 400);
                }
            }

            var now = DateTimeOffset.UtcNow;
            string newEmailPlain;
            if (effectiveEmail is not null)
            {
                user.Email = enc.Encrypt(effectiveEmail)!;
                user.EmailHash = enc.ComputeEmailHash(effectiveEmail);
                newEmailPlain = effectiveEmail;
            }
            else
            {
                newEmailPlain = enc.Decrypt(user.Email) ?? user.Email;
            }
            if (effectivePassword is not null)
                user.PasswordHash = passwords.Hash(effectivePassword);
            user.UpdatedAt = now;

            await db.SaveChangesAsync(ct);

            var svc = new AuthService(db, passwords, tokens, enc);
            var roles = await svc.GetUserRolesAsync(user, user.TenantId, ct);
            var newJwt = jwt.Sign(new StaffJwtClaims
            {
                Sub = user.Id.ToString(),
                Sid = auth.Sid?.ToString(),
                TenantId = user.TenantId?.ToString(),
                OrgId = user.OrganizationId?.ToString(),
                Email = newEmailPlain,
                Roles = roles,
            });
            AuthHttp.SetCookie(http, "auth_token", newJwt, AuthHttp.AccessTokenMaxAgeSeconds);
            return Results.Json(new { ok = true, email = newEmailPlain });
        }
        catch
        {
            return Results.Json(new { error = "Failed to update profile." }, statusCode: 400);
        }
    }

    private static async Task<JsonElement> ReadJsonObject(HttpContext http, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.Clone();
        }
        catch
        {
            // malformed -> treated as {}
        }
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsEmail(string value) =>
        value.Length > 0 && System.Net.Mail.MailAddress.TryCreate(value, out _);
}
