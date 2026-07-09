using OpenMercato.Core.Configuration;
using OpenMercato.Modules.Auth.Security;
using Xunit;

namespace OpenMercato.Tests.Auth;

/// <summary>
/// Golden-vector + behaviour tests for the per-tenant-DEK field encryption infra. The golden vectors
/// pin byte-for-byte compatibility with the upstream TypeScript runtime on a shared DB.
/// </summary>
public class EncryptionTests
{
    // Make-or-break: DerivedKms DEK derivation must match upstream exactly.
    private const string GoldenSecret = "testbench-shared-encryption-key-change-me";
    private const string GoldenTenantId = "1f30c451-6051-482b-94a4-bb1b39c3cf67";
    private const string GoldenDek = "BUyrYVulGxZUSBYI5jooypcDlxWWkLyiRs2kiQu/BkE=";

    // email_hash golden: plain hex SHA-256(lower(trim(email))).
    private const string GoldenEmail = "superadmin@acme.com";
    private const string GoldenEmailHash = "318d563681b988530d411f1afed2430ebb4d9653f397668cf694f05371380f6c";

    [Fact]
    public void DerivedKms_matches_golden_vector()
    {
        var kms = new DerivedKmsService(GoldenSecret);
        Assert.Equal(GoldenDek, kms.DeriveKey(GoldenTenantId));
    }

    [Fact]
    public void DerivedKms_is_deterministic_and_caches()
    {
        var kms = new DerivedKmsService(GoldenSecret);
        Assert.Equal(kms.DeriveKey(GoldenTenantId), kms.DeriveKey(GoldenTenantId));
    }

    [Fact]
    public void DerivedKms_without_secret_is_unhealthy_and_returns_null()
    {
        var kms = new DerivedKmsService((string?)null);
        Assert.False(kms.IsHealthy);
        Assert.Null(kms.DeriveKey(GoldenTenantId));
    }

    [Fact]
    public void EmailHash_matches_golden_vector()
    {
        Assert.Equal(GoldenEmailHash, EncryptionService.PlainHashForLookup(GoldenEmail));
    }

    [Fact]
    public void ComputeEmailHash_primary_output_is_plain_sha256()
    {
        var enc = new EncryptionService(new AppConfig("db", "redis", "local", "redis", "jwt-secret", 0));
        // Canonical write-side hash is the plain SHA-256 (not the legacy v2: HMAC).
        Assert.Equal(GoldenEmailHash, enc.ComputeEmailHash(GoldenEmail));
        // Normalization: uppercase + surrounding whitespace fold to the same hash.
        Assert.Equal(GoldenEmailHash, enc.ComputeEmailHash("  SuperAdmin@Acme.COM  "));
        // Lookup values always include the canonical plain hash.
        Assert.Contains(GoldenEmailHash, enc.EmailHashLookupValues(GoldenEmail));
    }

    [Fact]
    public void AesGcm_encrypt_decrypt_round_trips_with_derived_dek()
    {
        var kms = new DerivedKmsService(GoldenSecret);
        var dek = Convert.FromBase64String(kms.DeriveKey(GoldenTenantId)!);

        const string plain = "superadmin@acme.com";
        var payload = EncryptionService.EncryptWithKey(dek, plain);

        // Serialized shape is ivB64:ctB64:tagB64:v1.
        var parts = payload.Split(':');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[3]);
        Assert.NotEqual(plain, payload);

        Assert.Equal(plain, EncryptionService.DecryptWithKey(dek, payload));
    }

    [Fact]
    public void DecryptWithKey_returns_null_for_plaintext_or_wrong_format()
    {
        var dek = Convert.FromBase64String(new DerivedKmsService(GoldenSecret).DeriveKey(GoldenTenantId)!);
        Assert.Null(EncryptionService.DecryptWithKey(dek, "not-encrypted"));
        Assert.Null(EncryptionService.DecryptWithKey(dek, null));
    }

    [Fact]
    public void TenantDataEncryptionService_is_noop_when_no_map()
    {
        // InMemory provider can't run the raw-SQL map lookup → fail-soft → payload unchanged.
        using var db = AuthTestDb.Create();
        var svc = new TenantDataEncryptionService(new DerivedKmsService(GoldenSecret));
        var tenantId = Guid.Parse(GoldenTenantId);

        var payload = new Dictionary<string, object?> { ["Email"] = GoldenEmail, ["Name"] = "Super Admin" };
        var encrypted = svc.EncryptEntityPayload(db, "auth:user", tenantId, null, payload);

        Assert.Equal(GoldenEmail, encrypted["Email"]);      // untouched — no encryption applied
        Assert.Equal("Super Admin", encrypted["Name"]);

        var decrypted = svc.DecryptEntityPayload(db, "auth:user", tenantId, null, encrypted);
        Assert.Equal(GoldenEmail, decrypted["Email"]);      // still plaintext
    }

    [Fact]
    public void TenantDataEncryptionService_is_noop_without_tenant()
    {
        using var db = AuthTestDb.Create();
        var svc = new TenantDataEncryptionService(new DerivedKmsService(GoldenSecret));
        var payload = new Dictionary<string, object?> { ["Email"] = GoldenEmail };
        var result = svc.EncryptEntityPayload(db, "auth:user", tenantId: null, organizationId: null, payload);
        Assert.Equal(GoldenEmail, result["Email"]);
    }
}
