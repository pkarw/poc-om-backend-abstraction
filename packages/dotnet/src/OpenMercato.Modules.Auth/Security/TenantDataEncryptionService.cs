using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Data;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Per-tenant-DEK field encryption — the .NET port of upstream
/// <c>TenantDataEncryptionService</c> (packages/shared/src/lib/encryption/tenantDataEncryptionService.ts).
///
/// For an <c>entity_id</c> it resolves the applicable <c>encryption_maps</c> row (3-level scope
/// fallback: (entity,tenant,org) → (entity,tenant,null) → (entity,null,null); <c>is_active</c> and
/// not soft-deleted; <c>IS NOT DISTINCT FROM</c> semantics for null scope) and applies each field
/// rule using the tenant DEK from <see cref="DerivedKmsService"/>.
///
/// FAIL-SOFT: the map lookup runs as raw SQL. If the provider can't execute it (e.g. the EF InMemory
/// provider used by the unit tests) or no map exists, the payload is returned UNCHANGED — no
/// encryption/decryption happens. This keeps the interceptor a no-op wherever encryption is not
/// provisioned.
/// </summary>
public sealed class TenantDataEncryptionService
{
    private readonly DerivedKmsService _kms;
    // Cache resolved maps (hits only, keyed by requested scope) so InMemory's per-call throw stays cheap
    // and a real DB hits the table once per scope. Misses are intentionally NOT cached so a map seeded
    // mid-process (e.g. by the initial-tenant seeder) is picked up on the next save.
    private readonly ConcurrentDictionary<string, EncryptedFieldRule[]> _mapCache = new(StringComparer.Ordinal);

    public TenantDataEncryptionService(DerivedKmsService kms) => _kms = kms;

    /// <summary>
    /// Return a copy of <paramref name="payload"/> with the entity's mapped fields encrypted (and any
    /// hashField populated). No-op copy when there is no tenant, no DEK, or no map.
    /// </summary>
    public Dictionary<string, object?> EncryptEntityPayload(
        DbContext db, string entityId, Guid? tenantId, Guid? organizationId, IReadOnlyDictionary<string, object?> payload)
    {
        var clone = new Dictionary<string, object?>(payload);
        var dekBase64 = _kms.DeriveKey(tenantId?.ToString());
        if (dekBase64 is null) return clone;
        var fields = GetMap(db, entityId, tenantId, organizationId);
        if (fields is null || fields.Length == 0) return clone;

        var key = Convert.FromBase64String(dekBase64);
        foreach (var rule in fields)
        {
            var k = FindKey(clone.Keys, rule.Field);
            if (k is null) continue;
            var value = clone[k];
            if (value is null) continue;
            if (value is string s)
            {
                if (s.Length == 0) continue;          // skip empty
                if (IsEncryptedPayload(s)) continue;  // avoid double-encrypting
            }
            var serialized = value as string ?? JsonSerializer.Serialize(value);
            clone[k] = EncryptionService.EncryptWithKey(key, serialized);
            if (!string.IsNullOrEmpty(rule.HashField))
            {
                var hashKey = FindKey(clone.Keys, rule.HashField!) ?? rule.HashField!;
                clone[hashKey] = EncryptionService.PlainHashForLookup(serialized);
            }
        }
        return clone;
    }

    /// <summary>
    /// Return a copy of <paramref name="payload"/> with the entity's mapped fields decrypted. Values
    /// that aren't decryptable (plaintext, wrong key) are left unchanged. No-op copy when there is no
    /// tenant, no DEK, or no map.
    /// </summary>
    public Dictionary<string, object?> DecryptEntityPayload(
        DbContext db, string entityId, Guid? tenantId, Guid? organizationId, IReadOnlyDictionary<string, object?> payload)
    {
        var clone = new Dictionary<string, object?>(payload);
        var dekBase64 = _kms.DeriveKey(tenantId?.ToString());
        if (dekBase64 is null) return clone;
        var fields = GetMap(db, entityId, tenantId, organizationId);
        if (fields is null || fields.Length == 0) return clone;

        var key = Convert.FromBase64String(dekBase64);
        foreach (var rule in fields)
        {
            var k = FindKey(clone.Keys, rule.Field);
            if (k is null) continue;
            if (clone[k] is not string value) continue;
            var decrypted = MaybeDecrypt(key, value);
            if (decrypted is not null) clone[k] = decrypted;
        }
        return clone;
    }

    /// <summary>Convenience: decrypt a <see cref="User"/>'s email + name in place (entity_id "auth:user").</summary>
    public void DecryptUserInPlace(DbContext db, User user)
    {
        var decrypted = DecryptEntityPayload(db, "auth:user", user.TenantId, user.OrganizationId,
            new Dictionary<string, object?> { ["Email"] = user.Email, ["Name"] = user.Name });
        if (decrypted.TryGetValue("Email", out var e) && e is string es) user.Email = es;
        if (decrypted.TryGetValue("Name", out var n)) user.Name = n as string;
    }

    // --- map resolution (raw SQL, fail-soft) -------------------------------------------------------

    private EncryptedFieldRule[]? GetMap(DbContext db, string entityId, Guid? tenantId, Guid? organizationId)
    {
        var candidates = new (Guid? Tenant, Guid? Org)[]
        {
            (tenantId, organizationId),
            (tenantId, null),
            (null, null),
        };
        foreach (var (t, o) in candidates)
        {
            var tag = $"{entityId}|{t}|{o}";
            if (_mapCache.TryGetValue(tag, out var cached)) return cached;
            EncryptedFieldRule[]? fields;
            try
            {
                fields = FetchMap(db, entityId, t, o);
            }
            catch
            {
                // Provider can't run raw SQL (e.g. InMemory) — treat as "no encryption" everywhere.
                return null;
            }
            if (fields is not null)
            {
                _mapCache[tag] = fields;
                return fields;
            }
            // DB-confirmed miss for this scope → fall through to the next (broader) candidate.
        }
        return null;
    }

    private static EncryptedFieldRule[]? FetchMap(DbContext db, string entityId, Guid? tenantId, Guid? organizationId)
    {
        var conn = db.Database.GetDbConnection(); // throws for non-relational providers → caught by GetMap
        var tenantPredicate = tenantId is null ? "tenant_id IS NULL" : "tenant_id = @tenant";
        var orgPredicate = organizationId is null ? "organization_id IS NULL" : "organization_id = @org";

        var wasClosed = conn.State == ConnectionState.Closed;
        if (wasClosed) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT fields_json FROM encryption_maps " +
                $"WHERE entity_id = @entity AND {tenantPredicate} AND {orgPredicate} " +
                $"AND is_active = true AND deleted_at IS NULL LIMIT 1";
            AddParam(cmd, "@entity", entityId);
            if (tenantId is not null) AddParam(cmd, "@tenant", tenantId.Value);
            if (organizationId is not null) AddParam(cmd, "@org", organizationId.Value);

            var result = cmd.ExecuteScalar();
            if (result is null || result is DBNull) return null;
            var json = result as string ?? result.ToString();
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<EncryptedFieldRule>();
            return ParseFields(json);
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static EncryptedFieldRule[] ParseFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<EncryptedFieldRule>();
            var rules = new List<EncryptedFieldRule>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var field = el.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                if (string.IsNullOrEmpty(field)) continue;
                string? hashField = el.TryGetProperty("hashField", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                rules.Add(new EncryptedFieldRule(field!, hashField));
            }
            return rules.ToArray();
        }
        catch
        {
            return Array.Empty<EncryptedFieldRule>();
        }
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static string? MaybeDecrypt(byte[] key, string payload)
    {
        var first = EncryptionService.DecryptWithKey(key, payload);
        if (first is null) return null;
        // Handle accidental double-encryption: if the first pass still looks like a v1 payload, retry once.
        if (IsEncryptedPayload(first))
        {
            var second = EncryptionService.DecryptWithKey(key, first);
            return second ?? first;
        }
        return first;
    }

    private static bool IsEncryptedPayload(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 4 && parts[3] == "v1";
    }

    /// <summary>Resolve a map field name (snake_case) against the payload's keys, ignoring case/underscores.</summary>
    private static string? FindKey(IEnumerable<string> keys, string field)
    {
        var normField = Normalize(field);
        foreach (var k in keys)
            if (Normalize(k) == normField) return k;
        return null;
    }

    private static string Normalize(string value) => value.Replace("_", string.Empty).ToLowerInvariant();
}
