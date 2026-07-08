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
