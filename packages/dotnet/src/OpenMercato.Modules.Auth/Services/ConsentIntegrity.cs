using System.Security.Cryptography;
using System.Text;
using OpenMercato.Modules.Auth.Data;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Consent integrity hashing — a 1:1 port of upstream
/// packages/core/src/modules/auth/lib/consentIntegrity.ts. HMAC-SHA256 over a pipe-joined payload,
/// keyed by the CONSENT_INTEGRITY_SECRET → AUTH_SECRET → NEXTAUTH_SECRET → JWT_SECRET chain
/// (dev-only default when unset).
/// </summary>
public static class ConsentIntegrity
{
    private const string DevOnlySecret = "om-consent-integrity-dev-only-secret";

    private static string Secret()
    {
        foreach (var name in new[] { "CONSENT_INTEGRITY_SECRET", "AUTH_SECRET", "NEXTAUTH_SECRET", "JWT_SECRET" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return DevOnlySecret;
    }

    private static string NormalizeDate(DateTimeOffset? date) =>
        date is { } d ? d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : string.Empty;

    public static string Compute(UserConsent c)
    {
        var payload = string.Join('|',
            c.UserId.ToString(),
            c.ConsentType,
            c.IsGranted ? "true" : "false",
            NormalizeDate(c.GrantedAt),
            NormalizeDate(c.WithdrawnAt),
            c.IpAddress ?? string.Empty,
            c.Source ?? string.Empty);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret()));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Verify a stored hash. <paramref name="ipAddress"/>/<paramref name="source"/> must be the
    /// DECRYPTED plaintext values (upstream verifies against decrypted fields).
    /// </summary>
    public static bool Verify(UserConsent c, string? decryptedIp, string? decryptedSource, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        var probe = new UserConsent
        {
            UserId = c.UserId,
            ConsentType = c.ConsentType,
            IsGranted = c.IsGranted,
            GrantedAt = c.GrantedAt,
            WithdrawnAt = c.WithdrawnAt,
            IpAddress = decryptedIp,
            Source = decryptedSource,
        };
        var expected = Compute(probe);
        if (expected.Length != hash.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(hash));
    }
}
