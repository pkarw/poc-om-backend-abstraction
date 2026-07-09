using System.Security.Cryptography;
using System.Text;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Field-level AES-256-GCM primitives + email lookup hashing, mirroring
/// packages/shared/src/lib/encryption/aes.ts and packages/core/src/modules/auth/lib/emailHash.ts.
///
/// Encryption is AES-256-GCM with a random per-row 12-byte IV, serialized as
/// <c>ivB64:ciphertextB64:tagB64:v1</c> (byte-compatible with upstream <c>encryptWithAesGcm</c>).
/// The static <see cref="EncryptWithKey"/>/<see cref="DecryptWithKey"/> routines are keyed by an
/// explicit 32-byte key so both this app-level service and the per-tenant-DEK
/// <see cref="TenantDataEncryptionService"/> share one implementation.
///
/// Email lookup hash: <see cref="ComputeEmailHash"/> is the canonical upstream
/// <c>hashForLookup</c> — plain lowercase-hex <c>SHA-256(lower(trim(email)))</c> (no pepper).
/// <see cref="EmailHashLookupValues"/> ALSO returns the legacy keyed <c>v2:</c> HMAC candidate when a
/// pepper is configured, so reads still match rows written under the old scheme (spec 05 R47).
///
/// NOTE (ADR 0007): the instance <see cref="Encrypt"/>/<see cref="Decrypt"/> use a single app-level
/// key = SHA-256(JWT_SECRET). Field encryption at rest is now driven by the per-tenant DEK via the
/// SaveChanges interceptor + <see cref="TenantDataEncryptionService"/>; this app-key path remains for
/// any callers that still need a self-consistent local codec.
/// </summary>
public sealed class EncryptionService
{
    private const string LookupHashV2Prefix = "v2:";
    private readonly byte[] _key;

    public EncryptionService(AppConfig config)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(config.JwtSecret));
    }

    /// <summary>Encrypt plaintext to <c>iv:ct:tag:v1</c> with the app-level key. Null/empty passes through.</summary>
    public string? Encrypt(string? plain) =>
        string.IsNullOrEmpty(plain) ? plain : EncryptWithKey(_key, plain);

    /// <summary>Decrypt an app-level <c>iv:ct:tag:v1</c> payload. Null on any format/auth failure.</summary>
    public string? Decrypt(string? payload) => DecryptWithKey(_key, payload);

    /// <summary>
    /// Encrypt <paramref name="plain"/> to <c>ivB64:ctB64:tagB64:v1</c> with an explicit 32-byte key
    /// (AES-256-GCM, random 12-byte IV, 16-byte tag). Byte-compatible with upstream encryptWithAesGcm.
    /// </summary>
    public static string EncryptWithKey(byte[] key, string plain)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, plainBytes, cipher, tag);
        return string.Join(':',
            Convert.ToBase64String(iv),
            Convert.ToBase64String(cipher),
            Convert.ToBase64String(tag),
            "v1");
    }

    /// <summary>Decrypt an <c>iv:ct:tag:v1</c> payload with an explicit key. Null on format/auth failure.</summary>
    public static string? DecryptWithKey(byte[] key, string? payload)
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
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Canonical email lookup hash — plain lowercase-hex <c>SHA-256(lower(trim(email)))</c>
    /// (upstream <c>hashForLookup</c> / <c>computeEmailHash</c>). This is the write-side value.
    /// </summary>
    public string ComputeEmailHash(string email) => PlainHashForLookup(email);

    /// <summary>
    /// Read-side candidate hashes for email lookup: the canonical plain SHA-256 first, plus the legacy
    /// keyed <c>v2:</c> HMAC when a pepper is configured, so rows written under either scheme match.
    /// </summary>
    public string[] EmailHashLookupValues(string email)
    {
        var plain = PlainHashForLookup(email);
        var v2 = V2HashForLookup(email);
        return v2 is null || v2 == plain ? new[] { plain } : new[] { plain, v2 };
    }

    /// <summary>Plain lowercase-hex <c>SHA-256(lower(trim(value)))</c> (upstream hashForLookup).</summary>
    public static string PlainHashForLookup(string value)
    {
        var normalized = value.ToLowerInvariant().Trim();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>Legacy keyed lookup hash (<c>v2:</c> + HMAC-SHA256(pepper, lower.trim)), or null when no pepper.</summary>
    private static string? V2HashForLookup(string value)
    {
        var pepper = ResolveLookupPepper();
        if (pepper is null) return null;
        var normalized = value.ToLowerInvariant().Trim();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return LookupHashV2Prefix + Convert.ToHexString(digest).ToLowerInvariant();
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
