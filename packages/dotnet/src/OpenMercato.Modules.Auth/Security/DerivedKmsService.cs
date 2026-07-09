using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Deterministic per-tenant Data Encryption Key (DEK) derivation — the .NET port of upstream
/// <c>DerivedKmsService</c> (packages/shared/src/lib/encryption/kms.ts). Given a base secret it
/// derives a stable root key <c>SHA-256(secret)</c>, then for each tenant derives
/// <c>base64(PBKDF2(root, UTF8(tenantId), 310000, SHA-512, 32))</c>.
///
/// Because derivation is deterministic, two processes sharing the same secret derive identical
/// tenant DEKs (bidirectional parity with the upstream TypeScript app on a shared DB).
///
/// Secret resolution order (each trimmed with surrounding single/double quotes stripped):
/// TENANT_DATA_ENCRYPTION_FALLBACK_KEY → TENANT_DATA_ENCRYPTION_KEY → AUTH_SECRET → NEXTAUTH_SECRET →
/// dev default "om-dev-tenant-encryption" (only when NODE_ENV != "production"). In production with no
/// secret, <see cref="IsHealthy"/> is false and <see cref="DeriveKey"/> returns null (no encryption).
/// </summary>
public sealed class DerivedKmsService
{
    private const int Iterations = 310_000;
    private const int KeyLengthBytes = 32;
    private const string DevDefaultSecret = "om-dev-tenant-encryption";

    private readonly byte[]? _root;
    private readonly ConcurrentDictionary<string, string> _dekCache = new(StringComparer.Ordinal);

    /// <summary>Resolve the secret from the environment (the DI-registered variant).</summary>
    public DerivedKmsService() : this(ResolveSecret()) { }

    /// <summary>Construct with an explicit secret (used by unit tests / the golden vectors).</summary>
    public DerivedKmsService(string? secret)
    {
        _root = string.IsNullOrEmpty(secret) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>True when a secret was resolved so DEKs can be derived.</summary>
    public bool IsHealthy => _root is not null;

    /// <summary>
    /// Derive (and cache) the base64 DEK for <paramref name="tenantId"/>, or null when no tenant /
    /// no secret. <paramref name="tenantId"/> is the canonical tenant id string used verbatim as the
    /// PBKDF2 salt (make-or-break for cross-runtime parity).
    /// </summary>
    public string? DeriveKey(string? tenantId)
    {
        if (_root is null || string.IsNullOrEmpty(tenantId)) return null;
        return _dekCache.GetOrAdd(tenantId, tid =>
            Convert.ToBase64String(
                Rfc2898DeriveBytes.Pbkdf2(_root, Encoding.UTF8.GetBytes(tid), Iterations, HashAlgorithmName.SHA512, KeyLengthBytes)));
    }

    private static string? ResolveSecret()
    {
        foreach (var name in new[]
                 {
                     "TENANT_DATA_ENCRYPTION_FALLBACK_KEY",
                     "TENANT_DATA_ENCRYPTION_KEY",
                     "AUTH_SECRET",
                     "NEXTAUTH_SECRET",
                 })
        {
            var normalized = NormalizeEnv(Environment.GetEnvironmentVariable(name));
            if (normalized.Length > 0) return normalized;
        }
        var nodeEnv = Environment.GetEnvironmentVariable("NODE_ENV");
        return string.Equals(nodeEnv, "production", StringComparison.Ordinal) ? null : DevDefaultSecret;
    }

    /// <summary>Trim, then strip a single leading and a single trailing single/double quote (upstream normalizeEnv).</summary>
    private static string NormalizeEnv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == '\'' || trimmed[0] == '"'))
            trimmed = trimmed[1..];
        if (trimmed.Length > 0 && (trimmed[^1] == '\'' || trimmed[^1] == '"'))
            trimmed = trimmed[..^1];
        return trimmed;
    }
}
