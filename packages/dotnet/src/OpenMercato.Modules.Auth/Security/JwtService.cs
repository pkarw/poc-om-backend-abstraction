using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenMercato.Core.Configuration;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>Staff JWT claims (spec 05 R5), plus decoded standard claims on verify.</summary>
public sealed record StaffJwtClaims
{
    public required string Sub { get; init; }
    public string? Sid { get; init; }
    public string? TenantId { get; init; }
    public string? OrgId { get; init; }
    public string? Email { get; init; }
    public string[] Roles { get; init; } = Array.Empty<string>();
    public long? Iat { get; init; }
    public long? Exp { get; init; }
    /// <summary>True when the token verified only via the raw-secret legacy grace path.</summary>
    public bool LegacyToken { get; init; }
}

/// <summary>
/// Hand-rolled HS256 JWT, byte-compatible with upstream packages/shared/src/lib/auth/jwt.ts
/// (spec 05 R1-R5). Header {"alg":"HS256","typ":"JWT"}; unpadded base64url segments;
/// signature = HMAC-SHA256 over "header.payload" using the audience-derived signing key.
/// Default audience is <c>staff</c>, issuer <c>open-mercato</c>, TTL 28800 s (8 h).
/// The signing key is NEVER the raw JWT_SECRET: it is env JWT_STAFF_SECRET, else the
/// lowercase hex HMAC-SHA256(key=JWT_SECRET, msg="open-mercato:jwt:v1:staff") used as a
/// UTF-8 key string. Verification enforces aud/iss strictly and rejects expired tokens;
/// a legacy grace path (JWT_LEGACY_GRACE_MINUTES != 0/false/off) retries with the raw secret.
/// </summary>
public sealed class JwtService
{
    private const string DefaultIssuer = "open-mercato";
    private const string StaffAudience = "staff";
    private const string AudienceSecretLabel = "open-mercato:jwt:v1";
    public const int DefaultTtlSeconds = 60 * 60 * 8;

    private readonly string _baseSecret;

    public JwtService(AppConfig config)
    {
        _baseSecret = config.JwtSecret;
    }

    /// <summary>Sign a staff JWT. Claim order is our own; verification is signature-over-bytes.</summary>
    public string Sign(StaffJwtClaims claims, int? expiresInSec = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = now + (expiresInSec ?? DefaultTtlSeconds);
        var body = new Dictionary<string, object?>
        {
            ["iat"] = now,
            ["exp"] = exp,
            ["iss"] = DefaultIssuer,
            ["aud"] = StaffAudience,
            ["sub"] = claims.Sub,
            ["sid"] = claims.Sid,
            ["tenantId"] = claims.TenantId,
            ["orgId"] = claims.OrgId,
            ["email"] = claims.Email,
            ["roles"] = claims.Roles,
        };
        return Encode(body, DeriveAudienceSecret(StaffAudience));
    }

    private static string Encode(Dictionary<string, object?> body, string secret)
    {
        var header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
        var encHeader = Base64Url(Encoding.UTF8.GetBytes(header));
        var encBody = Base64Url(JsonSerializer.SerializeToUtf8Bytes(body));
        var data = $"{encHeader}.{encBody}";
        var sig = HmacSha256(secret, data);
        return $"{data}.{Base64Url(sig)}";
    }

    /// <summary>Verify a token and decode staff claims. Returns false on any failure.</summary>
    public bool TryVerify(string token, out StaffJwtClaims claims)
    {
        claims = null!;
        var payload = VerifyWithSecret(token, DeriveAudienceSecret(StaffAudience), enforceAudIss: true);
        var legacy = false;
        if (payload is null && LegacyGraceEnabled())
        {
            payload = VerifyWithSecret(token, _baseSecret, enforceAudIss: false);
            legacy = payload is not null;
        }
        if (payload is null) return false;
        claims = ToClaims(payload.Value, legacy);
        return true;
    }

    private JsonElement? VerifyWithSecret(string token, string secret, bool enforceAudIss)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        var data = $"{parts[0]}.{parts[1]}";
        var expected = Base64Url(HmacSha256(secret, data));
        var provided = Encoding.UTF8.GetBytes(parts[2]);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (provided.Length != expectedBytes.Length) return null;
        if (!CryptographicOperations.FixedTimeEquals(provided, expectedBytes)) return null;

        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecode(parts[1]));
        }
        catch
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number
            && now > expEl.GetInt64()) return null;
        if (enforceAudIss)
        {
            if (!(payload.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String
                  && aud.GetString() == StaffAudience)) return null;
            if (!(payload.TryGetProperty("iss", out var iss) && iss.ValueKind == JsonValueKind.String
                  && iss.GetString() == DefaultIssuer)) return null;
        }
        return payload;
    }

    private static StaffJwtClaims ToClaims(JsonElement p, bool legacy)
    {
        string? Str(string name) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? Num(string name) =>
            p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        string[] Roles()
        {
            if (p.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array)
                return r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!).ToArray();
            return Array.Empty<string>();
        }
        return new StaffJwtClaims
        {
            Sub = Str("sub") ?? string.Empty,
            Sid = Str("sid"),
            TenantId = Str("tenantId"),
            OrgId = Str("orgId"),
            Email = Str("email"),
            Roles = Roles(),
            Iat = Num("iat"),
            Exp = Num("exp"),
            LegacyToken = legacy,
        };
    }

    private string DeriveAudienceSecret(string audience)
    {
        var normalized = NormalizeAudience(audience);
        var overrideEnv = Environment.GetEnvironmentVariable($"JWT_{normalized.ToUpperInvariant()}_SECRET");
        if (!string.IsNullOrWhiteSpace(overrideEnv)) return overrideEnv;
        var label = $"{AudienceSecretLabel}:{normalized}";
        var digest = HmacSha256(_baseSecret, label);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string NormalizeAudience(string audience)
    {
        var lowered = audience.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        var pendingUnderscore = false;
        foreach (var c in lowered)
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                if (pendingUnderscore) { sb.Append('_'); pendingUnderscore = false; }
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                pendingUnderscore = true;
            }
        }
        return sb.ToString();
    }

    private static bool LegacyGraceEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("JWT_LEGACY_GRACE_MINUTES");
        return raw is not ("0" or "false" or "off");
    }

    private static byte[] HmacSha256(string keyUtf8, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyUtf8));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
