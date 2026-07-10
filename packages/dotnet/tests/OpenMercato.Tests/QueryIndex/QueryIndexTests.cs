using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.QueryIndex;
using OpenMercato.Modules.QueryIndex.Crud;
using OpenMercato.Modules.QueryIndex.Data;
using OpenMercato.Modules.QueryIndex.Lib;
using Xunit;

namespace OpenMercato.Tests.QueryIndex;

/// <summary>
/// Unit tests for the query_index read model: <see cref="QueryIndexCrudIndexer"/> projects a base row +
/// <c>cf:&lt;key&gt;</c> custom-field values (with the aggregate <c>search_text</c>) into
/// <c>entity_indexes.doc</c>; the <see cref="QueryIndexEngine"/> filters/sorts by a cf value; and delete
/// removes the projection row.
/// </summary>
public class QueryIndexTests
{
    private const string EntityType = "test:widget";
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static AppDbContext NewDb()
    {
        var registry = new ModuleRegistry(new IModule[] { new EntitiesModule(), new QueryIndexModule() });
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("qi-tests-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        return db;
    }

    private static QueryIndexCrudIndexer Indexer(AppDbContext db)
        => new(db, new CustomEntitiesStorageBaseRowResolver(db));

    private static void SeedStorage(AppDbContext db, string recordId, string docJson)
    {
        var now = DateTimeOffset.UtcNow;
        db.Set<CustomEntityStorage>().Add(new CustomEntityStorage
        {
            Id = Guid.NewGuid(),
            EntityType = EntityType,
            EntityId = recordId,
            OrganizationId = Org,
            TenantId = Tenant,
            Doc = docJson,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    private static void SeedCfValue(AppDbContext db, string recordId, string key, Action<CustomFieldValue> set)
    {
        var row = new CustomFieldValue
        {
            Id = Guid.NewGuid(),
            EntityId = EntityType,
            RecordId = recordId,
            OrganizationId = Org,
            TenantId = Tenant,
            FieldKey = key,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        set(row);
        db.Set<CustomFieldValue>().Add(row);
        db.SaveChanges();
    }

    [Fact]
    public async Task UpsertOne_projects_base_and_cf_keys_into_doc()
    {
        var db = NewDb();
        var recordId = Guid.NewGuid().ToString();
        SeedStorage(db, recordId, "{\"name\":\"Acme\",\"email\":\"a@b.c\"}");
        SeedCfValue(db, recordId, "priority", r => r.ValueInt = 3);
        SeedCfValue(db, recordId, "labels", r => r.ValueText = "alpha");
        SeedCfValue(db, recordId, "labels", r => r.ValueText = "beta");

        await Indexer(db).UpsertOneAsync(EntityType, recordId, Org, Tenant, "create");

        var row = await db.Set<EntityIndexRow>().SingleAsync(r => r.EntityId == recordId);
        var doc = DocJson.ParseObject(row.Doc);

        Assert.Equal("Acme", doc["name"]);
        Assert.Equal(3L, Convert.ToInt64(doc["cf:priority"]));
        var labels = Assert.IsAssignableFrom<System.Collections.IEnumerable>(doc["cf:labels"])
            .Cast<object?>().Select(x => x?.ToString()).ToList();
        Assert.Equal(new[] { "alpha", "beta" }, labels);
        // search_text aggregates the string values (excludes id/_id/_at fields).
        var searchText = Assert.IsType<string>(doc["search_text"]);
        Assert.Contains("Acme", searchText);
        Assert.Contains("alpha", searchText);
    }

    [Fact]
    public async Task UpsertOne_is_idempotent_and_updates_the_same_row()
    {
        var db = NewDb();
        var recordId = Guid.NewGuid().ToString();
        SeedStorage(db, recordId, "{\"name\":\"First\"}");
        await Indexer(db).UpsertOneAsync(EntityType, recordId, Org, Tenant, "create");

        // Change the base doc and re-index → still one row, updated content.
        var storage = await db.Set<CustomEntityStorage>().SingleAsync(s => s.EntityId == recordId);
        storage.Doc = "{\"name\":\"Second\"}";
        await db.SaveChangesAsync();
        await Indexer(db).UpsertOneAsync(EntityType, recordId, Org, Tenant, "update");

        Assert.Equal(1, await db.Set<EntityIndexRow>().CountAsync(r => r.EntityId == recordId));
        var doc = DocJson.ParseObject((await db.Set<EntityIndexRow>().SingleAsync(r => r.EntityId == recordId)).Doc);
        Assert.Equal("Second", doc["name"]);
    }

    [Fact]
    public async Task Engine_filters_and_sorts_by_cf_value()
    {
        var db = NewDb();
        var indexer = Indexer(db);

        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        var c = Guid.NewGuid().ToString();
        SeedStorage(db, a, "{\"name\":\"A\"}"); SeedCfValue(db, a, "tier", r => r.ValueText = "gold"); SeedCfValue(db, a, "score", r => r.ValueInt = 30);
        SeedStorage(db, b, "{\"name\":\"B\"}"); SeedCfValue(db, b, "tier", r => r.ValueText = "silver"); SeedCfValue(db, b, "score", r => r.ValueInt = 10);
        SeedStorage(db, c, "{\"name\":\"C\"}"); SeedCfValue(db, c, "tier", r => r.ValueText = "gold"); SeedCfValue(db, c, "score", r => r.ValueInt = 20);
        foreach (var id in new[] { a, b, c })
            await indexer.UpsertOneAsync(EntityType, id, Org, Tenant, "create");

        var engine = new QueryIndexEngine(db);

        // Filter cf:tier == gold, sort by cf:score desc → [a(30), c(20)].
        var result = await engine.QueryAsync(new QueryIndexRequest
        {
            EntityType = EntityType,
            TenantId = Tenant,
            OrganizationIds = new[] { Org },
            Filters = new[] { new IndexFilter("cf:tier", IndexFilterOp.Eq, "gold") },
            Sort = new[] { new IndexSort("cf:score", Descending: true) },
            Page = 1,
            PageSize = 20,
        });

        Assert.Equal(2, result.Total);
        Assert.Equal(new[] { a, c }, result.RecordIds);

        // cf_ prefix is normalized to cf: as well.
        var normalized = await engine.QueryAsync(new QueryIndexRequest
        {
            EntityType = EntityType,
            TenantId = Tenant,
            OrganizationIds = new[] { Org },
            Filters = new[] { new IndexFilter("cf_tier", IndexFilterOp.Eq, "silver") },
        });
        Assert.Equal(1, normalized.Total);
        Assert.Equal(new[] { b }, normalized.RecordIds);
    }

    [Fact]
    public async Task UpsertOne_writes_search_tokens_and_delete_removes_them()
    {
        var db = NewDb();
        var recordId = Guid.NewGuid().ToString();
        SeedStorage(db, recordId, "{\"name\":\"Acme Corporation\"}");
        var indexer = Indexer(db);
        await indexer.UpsertOneAsync(EntityType, recordId, Org, Tenant, "create");

        var tokens = await db.Set<SearchToken>().Where(t => t.EntityId == recordId).ToListAsync();
        Assert.NotEmpty(tokens);
        // search_text is one of the tokenized fields (aggregate search field is not skipped upstream).
        Assert.Contains(tokens, t => t.Field == "search_text");
        // The full token "acme" hash is present under search_text.
        var acmeHash = SearchTokenizer.HashToken("acme", new SearchConfig());
        Assert.Contains(tokens, t => t.Field == "search_text" && t.TokenHash == acmeHash);

        await indexer.DeleteOneAsync(EntityType, recordId, Org, Tenant);
        Assert.Equal(0, await db.Set<SearchToken>().CountAsync(t => t.EntityId == recordId));
    }

    [Fact]
    public async Task Engine_free_text_search_resolves_via_token_path_with_and_semantics()
    {
        var db = NewDb();
        var indexer = Indexer(db);
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        SeedStorage(db, a, "{\"name\":\"Acme Corporation\"}");
        SeedStorage(db, b, "{\"name\":\"Acme Industries\"}");
        await indexer.UpsertOneAsync(EntityType, a, Org, Tenant, "create");
        await indexer.UpsertOneAsync(EntityType, b, Org, Tenant, "create");

        var engine = new QueryIndexEngine(db);

        QueryIndexRequest Req(string search) => new()
        {
            EntityType = EntityType,
            TenantId = Tenant,
            OrganizationIds = new[] { Org },
            FullTextSearch = search,
        };

        // "acme" matches both records.
        var both = await engine.QueryAsync(Req("acme"));
        Assert.Equal(2, both.Total);
        Assert.Equal(new HashSet<string> { a, b }, both.RecordIds.ToHashSet());

        // "corporation" matches only record a.
        var one = await engine.QueryAsync(Req("corporation"));
        Assert.Equal(new[] { a }, one.RecordIds);

        // AND-of-tokens: "acme corporation" requires all hashes ⇒ only a (b lacks "corporation").
        var conj = await engine.QueryAsync(Req("acme corporation"));
        Assert.Equal(new[] { a }, conj.RecordIds);

        // A term present nowhere yields no matches (token path, not a substring fallback).
        var none = await engine.QueryAsync(Req("zzz"));
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task Engine_free_text_search_falls_back_to_ilike_when_scope_has_no_tokens()
    {
        var db = NewDb();
        // Seed the projection row directly (bypassing the indexer) so NO search_tokens exist for the scope.
        var recordId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        db.Set<EntityIndexRow>().Add(new EntityIndexRow
        {
            Id = Guid.NewGuid(),
            EntityType = EntityType,
            EntityId = recordId,
            OrganizationId = Org,
            TenantId = Tenant,
            Doc = "{\"name\":\"Acme Corporation\",\"search_text\":\"Acme Corporation\"}",
            IndexVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var engine = new QueryIndexEngine(db);
        // No tokens for the scope ⇒ the engine falls back to the search_text ilike substring match.
        var result = await engine.QueryAsync(new QueryIndexRequest
        {
            EntityType = EntityType,
            TenantId = Tenant,
            OrganizationIds = new[] { Org },
            FullTextSearch = "corpor",
        });
        Assert.Equal(new[] { recordId }, result.RecordIds);
    }

    [Fact]
    public async Task DeleteOne_removes_the_projection_row()
    {
        var db = NewDb();
        var recordId = Guid.NewGuid().ToString();
        SeedStorage(db, recordId, "{\"name\":\"Gone\"}");
        var indexer = Indexer(db);
        await indexer.UpsertOneAsync(EntityType, recordId, Org, Tenant, "create");
        Assert.Equal(1, await db.Set<EntityIndexRow>().CountAsync(r => r.EntityId == recordId));

        await indexer.DeleteOneAsync(EntityType, recordId, Org, Tenant);
        Assert.Equal(0, await db.Set<EntityIndexRow>().CountAsync(r => r.EntityId == recordId));
    }

    [Fact]
    public async Task UpsertOne_removes_row_when_base_record_is_gone()
    {
        var db = NewDb();
        var recordId = Guid.NewGuid().ToString();
        SeedStorage(db, recordId, "{\"name\":\"Temp\"}");
        var indexer = Indexer(db);
        await indexer.UpsertOneAsync(EntityType, recordId, Org, Tenant, "create");

        // Soft-delete the base record, re-index → projection row removed (doc == null path).
        var storage = await db.Set<CustomEntityStorage>().SingleAsync(s => s.EntityId == recordId);
        storage.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await indexer.UpsertOneAsync(EntityType, recordId, Org, Tenant, "update");

        Assert.Equal(0, await db.Set<EntityIndexRow>().CountAsync(r => r.EntityId == recordId));
    }
}
