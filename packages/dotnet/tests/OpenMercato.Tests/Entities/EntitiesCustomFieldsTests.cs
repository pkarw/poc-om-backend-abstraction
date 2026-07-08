using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.Entities.Crud;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.Entities.Lib;
using Xunit;

namespace OpenMercato.Tests.Entities;

/// <summary>
/// Unit tests for the entities custom-field engine: install-from-CE materializes module-declared field
/// sets into <c>custom_field_defs</c>; the real <see cref="EntitiesCrudCustomFields"/> codec round-trips
/// <c>cf_*</c> writes to bare-key reads; and per-kind/per-rule validation rejects bad values.
/// </summary>
public class EntitiesCustomFieldsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    // A test module declaring a CE field set (this is what a customer's module contributes).
    private sealed class WidgetFieldsModule : IModule
    {
        public string Id => "test_widget";
        public IReadOnlyList<string> AclFeatures { get; } = Array.Empty<string>();
        public IReadOnlyList<CustomFieldSet> CustomFieldSets { get; } = new[]
        {
            new CustomFieldSet("test:widget", new[]
            {
                new CustomFieldDefinition("priority", "integer", "Priority"),
                new CustomFieldDefinition("labels", "text", "Labels", Multi: true),
            }),
        };
        public void ConfigureServices(IServiceCollection services) { }
        public void ConfigureModel(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder) { }
        public void MapRoutes(IEndpointRouteBuilder routes) { }
    }

    private static (AppDbContext db, ModuleRegistry registry) NewDb()
    {
        var registry = new ModuleRegistry(new IModule[] { new EntitiesModule(), new WidgetFieldsModule() });
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("entities-tests-" + Guid.NewGuid().ToString("N"))
            .Options;
        var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        return (db, registry);
    }

    private static CommandContext Ctx() => new() { TenantId = Tenant, OrganizationId = Org };

    private static JsonElement Body(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task InstallFromCe_creates_defs_from_a_CustomFieldSet_and_is_idempotent()
    {
        var (db, registry) = NewDb();

        var first = await InstallFromCe.InstallAsync(db, registry, Tenant, organizationId: null);
        Assert.Equal(2, first.Created);
        Assert.Equal(0, first.Updated);

        var defs = await db.Set<CustomFieldDef>().Where(d => d.EntityId == "test:widget").ToListAsync();
        Assert.Equal(2, defs.Count);
        var priority = defs.Single(d => d.Key == "priority");
        Assert.Equal("integer", priority.Kind);
        Assert.Equal(Tenant, priority.TenantId);
        Assert.Null(priority.OrganizationId);
        Assert.Contains("\"label\":\"Priority\"", priority.ConfigJson);

        // Re-run → no changes (idempotent).
        var second = await InstallFromCe.InstallAsync(db, registry, Tenant, organizationId: null);
        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public async Task PersistAsync_then_MergeIntoDetail_round_trips_cf_values_as_bare_keys()
    {
        var (db, registry) = NewDb();
        await InstallFromCe.InstallAsync(db, registry, Tenant, organizationId: null);

        var codec = new EntitiesCrudCustomFields(db);
        var recordId = Guid.NewGuid().ToString();

        // Write via the cf_ request-key convention (integer scalar + multi-value text array).
        await codec.PersistAsync("test:widget", recordId, Body("{\"cf_priority\":3,\"cf_labels\":[\"a\",\"b\"]}"), Ctx());

        // Read back — values merged under BARE names on the record dict.
        var item = new Dictionary<string, object?> { ["id"] = recordId };
        await codec.MergeIntoDetailAsync("test:widget", item, Ctx());

        Assert.Equal(3, Convert.ToInt32(item["priority"]));
        var labels = Assert.IsAssignableFrom<System.Collections.IEnumerable>(item["labels"]).Cast<object?>().Select(x => x?.ToString()).ToList();
        Assert.Equal(new[] { "a", "b" }, labels);

        // Canonical customValues map is also present.
        var customValues = Assert.IsAssignableFrom<IDictionary<string, object?>>(item["customValues"]);
        Assert.True(customValues.ContainsKey("priority"));
    }

    [Fact]
    public async Task PersistAsync_rejects_values_failing_kind_validation()
    {
        var (db, _) = NewDb();

        // A def carrying a gte>=18 validation rule.
        var now = DateTimeOffset.UtcNow;
        db.Set<CustomFieldDef>().Add(new CustomFieldDef
        {
            Id = Guid.NewGuid(),
            EntityId = "test:widget",
            TenantId = Tenant,
            OrganizationId = null,
            Key = "age",
            Kind = "integer",
            ConfigJson = "{\"validation\":[{\"rule\":\"gte\",\"param\":18,\"message\":\"too young\"}]}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var codec = new EntitiesCrudCustomFields(db);
        var ex = await Assert.ThrowsAsync<CommandHttpException>(() =>
            codec.PersistAsync("test:widget", Guid.NewGuid().ToString(), Body("{\"cf_age\":10}"), Ctx()));
        Assert.Equal(400, ex.Status);

        // A valid value passes.
        await codec.PersistAsync("test:widget", Guid.NewGuid().ToString(), Body("{\"cf_age\":21}"), Ctx());
        Assert.Equal(1, await db.Set<CustomFieldValue>().CountAsync(v => v.FieldKey == "age"));
    }

    [Fact]
    public void Validation_evaluates_rules_per_kind()
    {
        var defs = new[]
        {
            new DefLike("age", "integer", JsonDocument.Parse("{\"validation\":[{\"rule\":\"required\",\"message\":\"req\"}]}").RootElement),
        };
        var missing = CustomFieldValidation.ValidateValuesAgainstDefs(
            new Dictionary<string, object?> { ["age"] = null }, defs);
        Assert.False(missing.Ok);
        Assert.Equal("req", missing.FieldErrors["cf_age"]);

        var ok = CustomFieldValidation.ValidateValuesAgainstDefs(
            new Dictionary<string, object?> { ["age"] = 5L }, defs);
        Assert.True(ok.Ok);
    }
}
