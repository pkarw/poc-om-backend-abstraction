using System.Security.Cryptography;
using System.Text;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Field-level encryption + email lookup hashing, mirroring
/// packages/shared/src/lib/encryption/aes.ts and packages/core/src/modules/auth/lib/emailHash.ts.
///
/// Encryption is AES-256-GCM with a random per-row 12-byte IV, serialized as
/// <c>ivB64:ciphertextB64:tagB64:v1</c> (byte-compatible with upstream <c>encryptWithAesGcm</c>).
///
/// Email lookup hash: when a pepper is configured (LOOKUP_HASH_PEPPER ->
/// TENANT_DATA_ENCRYPTION_FALLBACK_KEY -> TENANT_DATA_ENCRYPTION_KEY) the result is
/// <c>v2:</c> + lowercase-hex HMAC-SHA256(pepper, lower(trim(email))); with no pepper it is the
/// legacy unkeyed <c>SHA-256(lower(trim(email)))</c> hex. <see cref="EmailHashLookupValues"/>
/// returns both candidates so reads match either format (spec 05 R47).
///
/// DEVIATION (ADR 0007): upstream derives a per-tenant DEK from a KMS. This port uses a single
/// app-level key = SHA-256(JWT_SECRET). Email login stays correct because it keys on the
/// deterministic email_hash, not on the ciphertext.
/// </summary>
public sealed class EncryptionService
{
    private const string LookupHashV2Prefix = "v2:";
    private readonly byte[] _key;

    public EncryptionService(AppConfig config)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(config.JwtSecret));
    }

    /// <summary>Encrypt plaintext to <c>iv:ct:tag:v1</c>. Null/empty passes through unchanged.</summary>
    public string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        var iv = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(iv, plainBytes, cipher, tag);
        return string.Join(':',
            Convert.ToBase64String(iv),
            Convert.ToBase64String(cipher),
            Convert.ToBase64String(tag),
            "v1");
    }

    /// <summary>Decrypt an <c>iv:ct:tag:v1</c> payload. Returns null on any format/auth failure.</summary>
    public string? Decrypt(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var parts = payload.Split(':');
        if (parts.Length != 4 || parts[3] != "v1") return null;
        try
        {
            var iv = Convert.FromBase64String(parts[0]);
            var cipher = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, tag.Length);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compute the primary (write-side) email lookup hash.</summary>
    public string ComputeEmailHash(string email) => HashForLookup(email);

    /// <summary>Both candidate hashes (keyed v2 + legacy unkeyed) for read-side matching, deduped.</summary>
    public string[] EmailHashLookupValues(string email)
    {
        var primary = HashForLookup(email);
        var legacy = LegacyHashForLookup(email);
        return primary == legacy ? new[] { primary } : new[] { primary, legacy };
    }

    private static string HashForLookup(string value)
    {
        var pepper = ResolveLookupPepper();
        var normalized = value.ToLowerInvariant().Trim();
        if (pepper is null) return LegacyHashForLookup(value);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return LookupHashV2Prefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string LegacyHashForLookup(string value)
    {
        var normalized = value.ToLowerInvariant().Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string? ResolveLookupPepper()
    {
        foreach (var name in new[]
                 {
                     "LOOKUP_HASH_PEPPER",
                     "TENANT_DATA_ENCRYPTION_FALLBACK_KEY",
                     "TENANT_DATA_ENCRYPTION_KEY",
                 })
        {
            var candidate = Environment.GetEnvironmentVariable(name);
            if (candidate is null) continue;
            var normalized = candidate.Trim().Trim('\'', '"');
            if (normalized.Length > 0) return normalized;
        }
        return null;
    }
}
