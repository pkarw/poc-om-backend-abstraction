using Microsoft.AspNetCore.Http;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Minimal locale resolver mirroring the upstream <c>detectLocale()</c> precedence used by
/// <c>resolveTranslations()</c>: the <c>locale</c> cookie (when a supported locale), else the first
/// supported match from <c>Accept-Language</c>, else the default locale <c>en</c>. The resolved locale
/// is echoed in sidebar responses and written to the row's <c>locale</c> column (which is excluded from
/// all sidebar uniqueness scopes upstream).
/// </summary>
public static class SidebarLocale
{
    private static readonly string[] Supported = { "en", "pl", "es", "de" };
    private const string Default = "en";

    public static string Resolve(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue("locale", out var cookie) &&
            cookie is not null &&
            Supported.Contains(cookie))
            return cookie;

        var accept = http.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(accept))
        {
            foreach (var part in accept.Split(','))
            {
                var tag = part.Split(';')[0].Trim().ToLowerInvariant();
                if (tag.Length == 0) continue;
                var primary = tag.Split('-')[0];
                if (Supported.Contains(primary)) return primary;
            }
        }

        return Default;
    }
}
