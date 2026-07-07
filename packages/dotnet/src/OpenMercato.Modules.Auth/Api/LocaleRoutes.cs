using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET + POST /api/auth/locale (public). Mirrors upstream api/locale/route.ts. Validates against the
/// configured locales (en/de/es/pl) and sets a 1-year, non-HttpOnly <c>locale</c> cookie. POST returns
/// JSON; GET <b>307</b>-redirects to a sanitized local path.
/// </summary>
public sealed class LocaleRoutes : IAuthRouteGroup
{
    private const int LocaleCookieMaxAgeSeconds = 60 * 60 * 24 * 365; // 365d

    public static readonly IReadOnlySet<string> SupportedLocales =
        new HashSet<string>(StringComparer.Ordinal) { "en", "de", "es", "pl" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/locale", Get);
        routes.MapPost("/api/auth/locale", Post);
    }

    private static async Task<IResult> Post(HttpContext http, CancellationToken ct)
    {
        string? locale;
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            locale = doc.RootElement.ValueKind == JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("locale", out var v)
                     && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return Results.Json(new { error = "Bad request" }, statusCode: 400);
        }

        if (locale is null || !SupportedLocales.Contains(locale))
            return Results.Json(new { error = "Invalid locale" }, statusCode: 400);

        AuthHttp.SetCookie(http, "locale", locale, LocaleCookieMaxAgeSeconds, httpOnly: false);
        return Results.Json(new { ok = true });
    }

    private static IResult Get(HttpContext http)
    {
        var locale = http.Request.Query["locale"].ToString();
        if (string.IsNullOrEmpty(locale) || !SupportedLocales.Contains(locale))
            return Results.Json(new { error = "Invalid locale" }, statusCode: 400);

        var safePath = AuthHttp.SanitizeRedirectPath(
            http.Request.Query["redirect"], AuthHttp.AppBaseUrl(http), "/");
        AuthHttp.SetCookie(http, "locale", locale, LocaleCookieMaxAgeSeconds, httpOnly: false);
        var location = AuthHttp.RequestOrigin(http) + safePath;
        return Results.Redirect(location, permanent: false, preserveMethod: true); // 307
    }
}
