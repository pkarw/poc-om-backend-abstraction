using Microsoft.AspNetCore.Http;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Request/response helpers shared by the core auth route groups. Mirrors the small upstream
/// utilities: cookie parsing/writing, request-origin + app-base-url resolution, redirect
/// sanitization (packages/core/src/modules/auth/lib/safeRedirect.ts) and boolean-token parsing
/// (packages/shared/src/lib/boolean.ts). Cookie <c>secure</c> flag follows NODE_ENV=production.
/// </summary>
internal static class AuthHttp
{
    public const int AccessTokenMaxAgeSeconds = 60 * 60 * 8; // 8h

    private static readonly HashSet<string> TrueTokens =
        new(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "y", "on", "enable", "enabled" };
    private static readonly HashSet<string> FalseTokens =
        new(StringComparer.OrdinalIgnoreCase) { "0", "false", "no", "n", "off", "disable", "disabled" };

    public static bool IsProduction =>
        string.Equals(Environment.GetEnvironmentVariable("NODE_ENV"), "production", StringComparison.Ordinal);

    /// <summary>parseBooleanToken: true/false/null (unknown). Login only treats an explicit true as remember.</summary>
    public static bool? ParseBooleanToken(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;
        if (TrueTokens.Contains(trimmed)) return true;
        if (FalseTokens.Contains(trimmed)) return false;
        return null;
    }

    /// <summary>resolveRequestOrigin: <c>proto://host</c> from x-forwarded-* then Host.</summary>
    public static string RequestOrigin(HttpContext http)
    {
        var req = http.Request;
        var proto = FirstHeader(http, "x-forwarded-proto") ?? (req.IsHttps ? "https" : req.Scheme);
        var host = FirstHeader(http, "x-forwarded-host") ?? req.Host.Value ?? "localhost";
        return $"{proto}://{host}";
    }

    /// <summary>getAppBaseUrl: NEXT_PUBLIC_APP_URL || APP_URL || request origin.</summary>
    public static string AppBaseUrl(HttpContext http)
    {
        var env = Environment.GetEnvironmentVariable("NEXT_PUBLIC_APP_URL");
        if (!string.IsNullOrEmpty(env)) return env;
        env = Environment.GetEnvironmentVariable("APP_URL");
        if (!string.IsNullOrEmpty(env)) return env;
        return RequestOrigin(http);
    }

    /// <summary>sanitizeRedirectPath — same-origin, absolute-path, no <c>//</c> in pathname; else fallback.</summary>
    public static string SanitizeRedirectPath(string? rawRedirect, string baseUrl, string fallback)
    {
        if (string.IsNullOrEmpty(rawRedirect)) return fallback;
        try
        {
            var baseUri = new Uri(baseUrl, UriKind.Absolute);
            var resolved = new Uri(baseUri, rawRedirect);
            if (!string.Equals(resolved.GetLeftPart(UriPartial.Authority), baseUri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
                return fallback;
            var path = resolved.AbsolutePath;
            if (!path.StartsWith('/')) return fallback;
            if (path.Contains("//", StringComparison.Ordinal)) return fallback;
            return path + resolved.Query + resolved.Fragment;
        }
        catch
        {
            return fallback;
        }
    }

    public static string? Cookie(HttpContext http, string name) =>
        http.Request.Cookies.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static void SetCookie(HttpContext http, string name, string value, int? maxAgeSeconds,
        DateTimeOffset? expires = null, bool httpOnly = true)
    {
        var options = new CookieOptions
        {
            HttpOnly = httpOnly,
            Path = "/",
            SameSite = SameSiteMode.Lax,
            Secure = IsProduction,
        };
        if (expires.HasValue) options.Expires = expires;
        if (maxAgeSeconds.HasValue) options.MaxAge = TimeSpan.FromSeconds(maxAgeSeconds.Value);
        http.Response.Cookies.Append(name, value, options);
    }

    /// <summary>Clear a cookie (maxAge 0), preserving the flags upstream uses.</summary>
    public static void ClearCookie(HttpContext http, string name, bool httpOnly = false)
    {
        var options = new CookieOptions
        {
            HttpOnly = httpOnly,
            Path = "/",
            SameSite = SameSiteMode.Lax,
            Secure = IsProduction,
            MaxAge = TimeSpan.Zero,
            Expires = DateTimeOffset.UnixEpoch,
        };
        http.Response.Cookies.Append(name, string.Empty, options);
    }

    private static string? FirstHeader(HttpContext http, string name) =>
        http.Request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
}
