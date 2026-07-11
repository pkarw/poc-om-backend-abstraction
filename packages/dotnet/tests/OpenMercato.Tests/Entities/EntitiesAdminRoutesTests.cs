using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.Entities.Data;
using Xunit;

namespace OpenMercato.Tests.Entities;

/// <summary>HTTP tests for the entities admin surface: registry (/entities), definitions.manage/batch/
/// restore, encryption, sidebar-entities.</summary>
public class EntitiesAdminRoutesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private const string PersonEntity = "customers:customer_person_profile";

    private sealed class StubRequestContext : ICrudRequestContext
    {
        public Task<CommandContext?> ResolveAsync(HttpContext http) =>
            Task.FromResult<CommandContext?>(new CommandContext { TenantId = Tenant, OrganizationId = Org, OrganizationIds = new[] { Org } });
        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(true);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(Action<AppDbContext>? seed = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[] { new EntitiesModule() });
        builder.Services.AddSingleton(registry);
        var dbName = "entities-admin-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext());

        var app = builder.Build();
        new EntitiesModule().MapRoutes(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            seed?.Invoke(db);
            db.SaveChanges();
        }
        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static CustomFieldDef Def(string key, string kind, bool active = true, DateTimeOffset? deleted = null) => new()
    {
        Id = Guid.NewGuid(), EntityId = PersonEntity, TenantId = Tenant, OrganizationId = Org, Key = key, Kind = kind,
        ConfigJson = "{\"label\":\"" + key + "\"}", IsActive = active, DeletedAt = deleted,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ---- registry ------------------------------------------------------------------------------
    [Fact]
    public async Task Registry_lists_code_entity_with_field_count_and_custom_entity()
    {
        await using var h = await BuildAsync(db =>
        {
            db.Set<CustomFieldDef>().Add(Def("buying_role", "select"));
            db.Set<CustomFieldDef>().Add(Def("newsletter_opt_in", "boolean"));
            db.Set<CustomEntity>().Add(new CustomEntity { Id = Guid.NewGuid(), EntityId = "custom:project", Label = "Projects", TenantId = Tenant, ShowInSidebar = true, IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        });

        var json = await ReadJson(await h.Client.GetAsync("/api/entities/entities"));
        var items = json.GetProperty("items").EnumerateArray().ToList();

        var person = items.Single(i => i.GetProperty("entityId").GetString() == PersonEntity);
        Assert.Equal("code", person.GetProperty("source").GetString());
        Assert.Equal(2, person.GetProperty("count").GetInt32());

        var project = items.Single(i => i.GetProperty("entityId").GetString() == "custom:project");
        Assert.Equal("custom", project.GetProperty("source").GetString());
        Assert.Equal("Projects", project.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Registry_post_creates_then_delete_soft_removes()
    {
        await using var h = await BuildAsync();
        var post = await h.Client.PostAsync("/api/entities/entities", Body("{\"entityId\":\"custom:widget\",\"label\":\"Widgets\",\"showInSidebar\":true}"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.True((await ReadJson(post)).GetProperty("ok").GetBoolean());

        var after = await ReadJson(await h.Client.GetAsync("/api/entities/entities"));
        Assert.Contains(after.GetProperty("items").EnumerateArray(), i => i.GetProperty("entityId").GetString() == "custom:widget");

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/entities/entities") { Content = Body("{\"entityId\":\"custom:widget\"}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var final = await ReadJson(await h.Client.GetAsync("/api/entities/entities"));
        Assert.DoesNotContain(final.GetProperty("items").EnumerateArray(), i => i.GetProperty("entityId").GetString() == "custom:widget");
    }

    [Fact]
    public async Task Registry_post_rejects_bad_entity_id()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/entities/entities", Body("{\"entityId\":\"NoColon\",\"label\":\"x\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- definitions.manage --------------------------------------------------------------------
    [Fact]
    public async Task Manage_returns_active_defs_and_tombstoned_keys()
    {
        await using var h = await BuildAsync(db =>
        {
            db.Set<CustomFieldDef>().Add(Def("status", "select"));
            db.Set<CustomFieldDef>().Add(Def("old_field", "text", active: false, deleted: DateTimeOffset.UtcNow));
        });

        var json = await ReadJson(await h.Client.GetAsync("/api/entities/definitions.manage?entityId=" + PersonEntity));
        var keys = json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("status", keys);
        Assert.DoesNotContain("old_field", keys);
        Assert.Contains("old_field", json.GetProperty("deletedKeys").EnumerateArray().Select(k => k.GetString()));
        Assert.True(json.GetProperty("settings").GetProperty("singleFieldsetPerRecord").GetBoolean());
    }

    [Fact]
    public async Task Manage_requires_entity_id()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/entities/definitions.manage")).StatusCode);
    }

    // ---- definitions.batch ---------------------------------------------------------------------
    [Fact]
    public async Task Batch_upserts_definitions_with_priority_and_manage_reflects_them()
    {
        await using var h = await BuildAsync();
        var payload = "{\"entityId\":\"" + PersonEntity + "\",\"definitions\":[" +
            "{\"key\":\"tier\",\"kind\":\"select\",\"configJson\":{\"label\":\"Tier\",\"options\":[\"gold\"]}}," +
            "{\"key\":\"notes\",\"kind\":\"multiline\"}]}";
        var res = await h.Client.PostAsync("/api/entities/definitions.batch", Body(payload));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());

        var manage = await ReadJson(await h.Client.GetAsync("/api/entities/definitions.manage?entityId=" + PersonEntity));
        var items = manage.GetProperty("items").EnumerateArray().ToList();
        var tier = items.Single(i => i.GetProperty("key").GetString() == "tier");
        Assert.Equal(0, tier.GetProperty("configJson").GetProperty("priority").GetInt32());
        var notes = items.Single(i => i.GetProperty("key").GetString() == "notes");
        Assert.Equal(1, notes.GetProperty("configJson").GetProperty("priority").GetInt32());
        // multiline default editor
        Assert.Equal("markdown", notes.GetProperty("configJson").GetProperty("editor").GetString());
    }

    [Fact]
    public async Task Batch_empty_definitions_is_ok()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/entities/definitions.batch", Body("{\"entityId\":\"" + PersonEntity + "\",\"definitions\":[]}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- definitions.restore -------------------------------------------------------------------
    [Fact]
    public async Task Restore_untombstones_a_deleted_def()
    {
        await using var h = await BuildAsync(db =>
            db.Set<CustomFieldDef>().Add(Def("archived", "text", active: false, deleted: DateTimeOffset.UtcNow)));

        var res = await h.Client.PostAsync("/api/entities/definitions.restore", Body("{\"entityId\":\"" + PersonEntity + "\",\"key\":\"archived\"}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var manage = await ReadJson(await h.Client.GetAsync("/api/entities/definitions.manage?entityId=" + PersonEntity));
        Assert.Contains(manage.GetProperty("items").EnumerateArray(), i => i.GetProperty("key").GetString() == "archived");
    }

    [Fact]
    public async Task Restore_missing_returns_404()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/entities/definitions.restore", Body("{\"entityId\":\"" + PersonEntity + "\",\"key\":\"nope\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ---- encryption ----------------------------------------------------------------------------
    [Fact]
    public async Task Encryption_returns_seeded_map_and_empty_for_unknown()
    {
        await using var h = await BuildAsync(db => db.Set<EncryptionMap>().Add(new EncryptionMap
        {
            Id = Guid.NewGuid(), EntityId = "auth:user", TenantId = Tenant,
            FieldsJson = "[{\"field\":\"email\",\"hashField\":\"email_hash\"}]", IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }));

        var got = await ReadJson(await h.Client.GetAsync("/api/entities/encryption?entityId=auth:user"));
        var fields = got.GetProperty("fields").EnumerateArray().ToList();
        Assert.Single(fields);
        Assert.Equal("email", fields[0].GetProperty("field").GetString());

        var empty = await ReadJson(await h.Client.GetAsync("/api/entities/encryption?entityId=customers:customer_deal"));
        Assert.Empty(empty.GetProperty("fields").EnumerateArray());
        Assert.True(empty.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Encryption_requires_entity_id()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/entities/encryption")).StatusCode);
    }

    // ---- sidebar-entities ----------------------------------------------------------------------
    [Fact]
    public async Task Sidebar_lists_showInSidebar_entities_with_href()
    {
        await using var h = await BuildAsync(db =>
        {
            db.Set<CustomEntity>().Add(new CustomEntity { Id = Guid.NewGuid(), EntityId = "custom:project", Label = "Projects", TenantId = Tenant, ShowInSidebar = true, IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.Set<CustomEntity>().Add(new CustomEntity { Id = Guid.NewGuid(), EntityId = "custom:hidden", Label = "Hidden", TenantId = Tenant, ShowInSidebar = false, IsActive = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        });

        var json = await ReadJson(await h.Client.GetAsync("/api/entities/sidebar-entities"));
        var items = json.GetProperty("items").EnumerateArray().ToList();
        var one = Assert.Single(items);
        Assert.Equal("custom:project", one.GetProperty("entityId").GetString());
        Assert.Equal("/backend/entities/user/custom%3Aproject/records", one.GetProperty("href").GetString());
    }
}
