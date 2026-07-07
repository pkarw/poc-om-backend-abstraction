using System.Security.Cryptography;
using System.Text;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Opaque-token generation and HMAC hashing for session, password-reset and invite tokens
/// (spec 05 R10). Raw token = 32 random bytes rendered as 64 lowercase hex chars.
/// Stored value = lowercase hex HMAC-SHA256(secret, rawToken) where secret is the first set of
/// AUTH_TOKEN_SECRET -> AUTH_SECRET -> NEXTAUTH_SECRET -> JWT_SECRET (dev default
/// 'om-auth-token-dev-only-secret'). Only the hash is ever persisted.
/// </summary>
public sealed class TokenHasher
{
    private const string DevDefaultSecret = "om-auth-token-dev-only-secret";
    private readonly string _secret;

    public TokenHasher()
    {
        _secret = ResolveSecret();
    }

    /// <summary>Generate a fresh raw token (64 lowercase hex chars). Return the raw value to the caller; store only <see cref="Hash"/>.</summary>
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Lowercase hex HMAC-SHA256 of the raw token under the configured secret.</summary>
    public string Hash(string raw)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string ResolveSecret()
    {
        foreach (var name in new[] { "AUTH_TOKEN_SECRET", "AUTH_SECRET", "NEXTAUTH_SECRET", "JWT_SECRET" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return DevDefaultSecret;
    }
}
