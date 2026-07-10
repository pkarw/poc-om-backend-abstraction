using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Customers.Data;
using Xunit;

namespace OpenMercato.Tests.Auth;

/// <summary>
/// End-to-end tests for Stage-2 per-tenant-DEK field encryption on the CUSTOMERS module: the
/// SaveChanges encrypt interceptor + the materialization DECRYPT interceptor, exercised against a real
/// relational provider (SQLite in-memory) with a provisioned <c>encryption_maps</c> row so the
/// interceptors actually fire (the InMemory provider used elsewhere fail-softs to a no-op).
/// </summary>
public sealed class FieldEncryptionInterceptorTests : IDisposable
{
    private const string Secret = "testbench-shared-encryption-key-change-me";
    private static readonly Guid TenantId = Guid.Parse("1f30c451-6051-482b-94a4-bb1b39c3cf67");
    private static readonly Guid OrgId = Guid.Parse("2a40d562-7162-593c-a5b5-cc2c4ad4d078");

    private readonly SqliteConnection _conn;
    private readonly ModuleRegistry _registry;
    private readonly TenantDataEncryptionService _encryption;
    private readonly TenantEncryptionInterceptor _saveInterceptor;
    private readonly TenantDecryptionMaterializationInterceptor _readInterceptor;

    public FieldEncryptionInterceptorTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        CreateSchema();
        SeedCommentEncryptionMap();

        _registry = new ModuleRegistry(new IModule[] { new AuthModule(), new CustomersModule() });
        _encryption = new TenantDataEncryptionService(new DerivedKmsService(Secret));
        _saveInterceptor = new TenantEncryptionInterceptor(_encryption, _registry);
        _readInterceptor = new TenantDecryptionMaterializationInterceptor(_encryption, _registry);
    }

    public void Dispose() => _conn.Dispose();

    private AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(_saveInterceptor, _readInterceptor)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new AppDbContext(options, _registry);
    }

    // Only the two tables the CustomerComment path touches: the map lookup table + the comment table.
    private void CreateSchema()
    {
        Exec(@"CREATE TABLE encryption_maps (
                 id TEXT, entity_id TEXT, tenant_id TEXT, organization_id TEXT,
                 fields_json TEXT, is_active INTEGER, created_at TEXT, updated_at TEXT, deleted_at TEXT);");
        Exec(@"CREATE TABLE customer_comments (
                 id TEXT PRIMARY KEY, organization_id TEXT, tenant_id TEXT, body TEXT NOT NULL,
                 author_user_id TEXT, appearance_icon TEXT, appearance_color TEXT,
                 created_at TEXT, updated_at TEXT, deleted_at TEXT, entity_id TEXT, deal_id TEXT);");
    }

    private void SeedCommentEncryptionMap()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO encryption_maps
            (id, entity_id, tenant_id, organization_id, fields_json, is_active)
            VALUES (@id, 'customers:customer_comment', @tenant, @org, '[{""field"":""body""}]', 1);";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("@tenant", TenantId);
        cmd.Parameters.AddWithValue("@org", OrgId);
        cmd.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? RawBody(Guid id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT body FROM customer_comments WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    private CustomerComment NewComment(Guid id, string body) => new()
    {
        Id = id,
        OrganizationId = OrgId,
        TenantId = TenantId,
        Body = body,
        AppearanceIcon = "chat",
        AppearanceColor = "#abc",
        EntityId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static bool IsCiphertext(string? s) => s is not null && s.Split(':') is { Length: 4 } p && p[3] == "v1";

    [Fact]
    public void Write_encrypts_body_at_rest_read_decrypts_it()
    {
        var id = Guid.NewGuid();
        using (var db = NewContext())
        {
            db.Set<CustomerComment>().Add(NewComment(id, "Hello confidential"));
            db.SaveChanges();
        }

        // At rest: ciphertext, not plaintext.
        Assert.True(IsCiphertext(RawBody(id)), "body column should be encrypted at rest");
        Assert.DoesNotContain("Hello confidential", RawBody(id));

        // Read: materialization interceptor decrypts transparently.
        using (var db = NewContext())
        {
            var c = db.Set<CustomerComment>().Single(x => x.Id == id);
            Assert.Equal("Hello confidential", c.Body);
            Assert.Equal("chat", c.AppearanceIcon); // non-encrypted field intact
        }
    }

    [Fact]
    public void Tracked_round_trip_updates_one_field_without_data_loss()
    {
        var id = Guid.NewGuid();
        using (var db = NewContext())
        {
            db.Set<CustomerComment>().Add(NewComment(id, "Original body"));
            db.SaveChanges();
        }

        var entityId = new CustomerComment();
        // Reload → verify decrypted + Unchanged (snapshot captured the DECRYPTED state), then edit one field.
        using (var db = NewContext())
        {
            var c = db.Set<CustomerComment>().Single(x => x.Id == id);
            entityId.EntityId = c.EntityId;
            Assert.Equal("Original body", c.Body);
            // CRITICAL: decrypt in InitializedInstance means EF snapshots plaintext → no spurious modify.
            Assert.Equal(EntityState.Unchanged, db.Entry(c).State);

            c.AppearanceColor = "#def"; // touch a NON-encrypted field only
            db.SaveChanges();
        }

        // The untouched encrypted field survived (no data loss / no double-encryption corruption).
        using (var db = NewContext())
        {
            var c = db.Set<CustomerComment>().Single(x => x.Id == id);
            Assert.Equal("Original body", c.Body);
            Assert.Equal("#def", c.AppearanceColor);
            Assert.Equal(entityId.EntityId, c.EntityId);
        }
        Assert.True(IsCiphertext(RawBody(id)), "body still encrypted at rest after update");
    }

    [Fact]
    public void No_op_save_of_reloaded_entity_does_not_rewrite_ciphertext()
    {
        var id = Guid.NewGuid();
        using (var db = NewContext())
        {
            db.Set<CustomerComment>().Add(NewComment(id, "Stable body"));
            db.SaveChanges();
        }
        var cipherBefore = RawBody(id);

        using (var db = NewContext())
        {
            var c = db.Set<CustomerComment>().Single(x => x.Id == id);
            Assert.Equal(EntityState.Unchanged, db.Entry(c).State);
            db.SaveChanges(); // no user change → interceptor must not re-encrypt (which would change the IV)
        }

        Assert.Equal(cipherBefore, RawBody(id)); // byte-identical → nothing was rewritten
    }

    [Fact]
    public void Decrypt_of_plaintext_is_idempotent_and_leaves_value_unchanged()
    {
        using var db = NewContext();
        // A value that is NOT a valid iv:ct:tag:v1 payload must be returned unchanged (never nulled),
        // so the read interceptor + Stage-1 explicit decrypt helpers coexist (double-decrypt = no-op).
        var payload = new Dictionary<string, object?> { ["Body"] = "already plaintext" };
        var once = _encryption.DecryptEntityPayload(db, "customers:customer_comment", TenantId, OrgId, payload);
        Assert.Equal("already plaintext", once["Body"]);

        var twice = _encryption.DecryptEntityPayload(db, "customers:customer_comment", TenantId, OrgId, once);
        Assert.Equal("already plaintext", twice["Body"]);

        // And a genuine encrypt→decrypt→decrypt sequence yields plaintext both times (idempotent).
        var enc = _encryption.EncryptEntityPayload(db, "customers:customer_comment", TenantId, OrgId,
            new Dictionary<string, object?> { ["Body"] = "secret" });
        Assert.True(IsCiphertext(enc["Body"] as string));
        var dec1 = _encryption.DecryptEntityPayload(db, "customers:customer_comment", TenantId, OrgId, enc);
        Assert.Equal("secret", dec1["Body"]);
        var dec2 = _encryption.DecryptEntityPayload(db, "customers:customer_comment", TenantId, OrgId, dec1);
        Assert.Equal("secret", dec2["Body"]);
    }
}
