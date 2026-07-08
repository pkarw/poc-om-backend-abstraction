using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Dictionaries.Data;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Customers;

/// <summary>
/// End-to-end HTTP tests for the customers Phase-2 dictionaries &amp; settings surface. Exercises the
/// reflection-registered command handlers via the real CommandBus: dictionary CRUD (201-by-mode,
/// upsert unchanged→200, kind mapping, org-inheritance list, role_type_in_use 409), the currency
/// route reading the generic dictionaries module, kind-settings upsert, and the three settings facets
/// (address-format, dictionary-sort-modes, stuck-threshold) with their defaults + feature gating.
/// </summary>
public class CustomersPhase2Tests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _authenticated;
        private readonly bool _hasFeatures;
        public StubRequestContext(bool authenticated = true, bool hasFeatures = true)
        { _authenticated = authenticated; _hasFeatures = hasFeatures; }

        public Task<CommandContext?> ResolveAsync(HttpContext http)
        {
            if (!_authenticated) return Task.FromResult<CommandContext?>(null);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in http.Request.Headers) headers[h.Key] = h.Value.ToString();
            return Task.FromResult<CommandContext?>(new CommandContext
            {
                TenantId = Tenant, OrganizationId = Org, UserId = User,
                OrganizationIds = new[] { Org }, AllowedOrganizationIds = new[] { Org }, Headers = headers,
            });
        }

        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(_hasFeatures);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(StubRequestContext? requestContext = null, Action<AppDbContext>? seed = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(),
            new OpenMercato.Modules.Directory.DirectoryModule(),
            new EntitiesModule(),
            new QueryIndexModule(),
            new OpenMercato.Modules.Dictionaries.DictionariesModule(),
            new CustomersModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "customers-p2-tests-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => requestContext ?? new StubRequestContext());

        var app = builder.Build();
        new CustomersModule().MapRoutes(app);

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

    // ---- dictionary CRUD ----------------------------------------------------------------------

    [Fact]
    public async Task Dictionary_create_returns_201_and_lists_with_sortmode()
    {
        await using var h = await BuildAsync();
        var create = await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"vip\",\"color\":\"#ABCDEF\"}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await ReadJson(create);
        Assert.Equal("vip", body.GetProperty("value").GetString());
        Assert.Equal("vip", body.GetProperty("label").GetString());
        Assert.Equal("#abcdef", body.GetProperty("color").GetString());
        Assert.False(body.GetProperty("isInherited").GetBoolean());
        var id = body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/statuses"));
        Assert.Equal("label_asc", list.GetProperty("sortMode").GetString());
        var items = list.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("vip", items[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Dictionary_create_duplicate_value_returns_200_unchanged()
    {
        await using var h = await BuildAsync();
        var first = await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"active\"}"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await ReadJson(first)).GetProperty("id").GetString();

        var again = await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"active\"}"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstId, (await ReadJson(again)).GetProperty("id").GetString());

        // Same value, changed label → upsert-update, still 200.
        var updated = await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"active\",\"label\":\"Active!\"}"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Active!", (await ReadJson(updated)).GetProperty("label").GetString());
    }

    [Fact]
    public async Task Dictionary_kind_mapping_stores_mapped_kind()
    {
        await using var h = await BuildAsync();
        var create = await h.Client.PostAsync("/api/customers/dictionaries/deal-statuses", Body("{\"value\":\"won\"}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Set<CustomerDictionaryEntry>().AnyAsync(e => e.Kind == "deal_status" && e.Value == "won"));

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/deal-statuses"));
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Dictionary_patch_updates_then_delete_removes()
    {
        await using var h = await BuildAsync();
        var id = (await ReadJson(await h.Client.PostAsync("/api/customers/dictionaries/sources", Body("{\"value\":\"web\"}")))).GetProperty("id").GetString();

        var patch = await h.Client.PatchAsync($"/api/customers/dictionaries/sources/{id}", Body("{\"label\":\"Website\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Website", (await ReadJson(patch)).GetProperty("label").GetString());

        var del = await h.Client.DeleteAsync($"/api/customers/dictionaries/sources/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.True((await ReadJson(del)).GetProperty("success").GetBoolean());

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/sources"));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Dictionary_patch_no_changes_returns_400()
    {
        await using var h = await BuildAsync();
        var id = (await ReadJson(await h.Client.PostAsync("/api/customers/dictionaries/sources", Body("{\"value\":\"web\"}")))).GetProperty("id").GetString();
        var patch = await h.Client.PatchAsync($"/api/customers/dictionaries/sources/{id}", Body("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
    }

    [Fact]
    public async Task Dictionary_patch_missing_returns_404_with_exact_message()
    {
        await using var h = await BuildAsync();
        var patch = await h.Client.PatchAsync($"/api/customers/dictionaries/sources/{Guid.NewGuid()}", Body("{\"label\":\"X\"}"));
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal("Dictionary entry not found", (await ReadJson(patch)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Dictionary_person_company_role_in_use_blocks_delete_with_409()
    {
        var entryId = Guid.NewGuid();
        await using var h = await BuildAsync(seed: db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<CustomerDictionaryEntry>().Add(new CustomerDictionaryEntry
            {
                Id = entryId, OrganizationId = Org, TenantId = Tenant, Kind = "person_company_role",
                Value = "decision_maker", NormalizedValue = "decision_maker", Label = "Decision Maker", CreatedAt = now, UpdatedAt = now,
            });
            db.Set<CustomerEntityRole>().Add(new CustomerEntityRole
            {
                Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, EntityType = "person",
                EntityId = Guid.NewGuid(), UserId = User, RoleType = "decision_maker", CreatedAt = now, UpdatedAt = now,
            });
        });

        var del = await h.Client.DeleteAsync($"/api/customers/dictionaries/person-company-roles/{entryId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        var body = await ReadJson(del);
        Assert.Equal("role_type_in_use", body.GetProperty("code").GetString());
        Assert.Equal(1, body.GetProperty("usageCount").GetInt32());

        // The list surfaces usageCount for person_company_role.
        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/person-company-roles"));
        Assert.Equal(1, list.GetProperty("items")[0].GetProperty("usageCount").GetInt32());
    }

    [Fact]
    public async Task Dictionary_requires_authentication()
    {
        await using var h = await BuildAsync(new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/customers/dictionaries/statuses");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- currency -----------------------------------------------------------------------------

    [Fact]
    public async Task Currency_dictionary_returns_entries()
    {
        var dictId = Guid.NewGuid();
        await using var h = await BuildAsync(seed: db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Set<Dictionary>().Add(new Dictionary
            {
                Id = dictId, OrganizationId = Org, TenantId = Tenant, Key = "currency", Name = "Currencies",
                IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now,
            });
            db.Set<DictionaryEntry>().Add(new DictionaryEntry
            {
                Id = Guid.NewGuid(), DictionaryId = dictId, OrganizationId = Org, TenantId = Tenant,
                Value = "EUR", NormalizedValue = "eur", Label = "Euro", CreatedAt = now, UpdatedAt = now,
            });
        });

        var res = await h.Client.GetAsync("/api/customers/dictionaries/currency");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal(dictId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("EUR", body.GetProperty("entries")[0].GetProperty("value").GetString());
        Assert.Equal("Euro", body.GetProperty("entries")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Currency_dictionary_missing_returns_404()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.GetAsync("/api/customers/dictionaries/currency");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("Currency dictionary is not configured yet.", (await ReadJson(res)).GetProperty("error").GetString());
    }

    // ---- kind-settings ------------------------------------------------------------------------

    [Fact]
    public async Task KindSettings_empty_then_upsert_roundtrip()
    {
        await using var h = await BuildAsync();
        var empty = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/kind-settings"));
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());

        var patch = await h.Client.PatchAsync("/api/customers/dictionaries/kind-settings",
            Body("{\"kind\":\"status\",\"selectionMode\":\"multi\",\"visibleInTags\":true,\"sortOrder\":3}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = await ReadJson(patch);
        Assert.Equal("status", body.GetProperty("kind").GetString());
        Assert.Equal("multi", body.GetProperty("selectionMode").GetString());
        Assert.Equal(3, body.GetProperty("sortOrder").GetInt32());

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/kind-settings"));
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        Assert.Equal("multi", list.GetProperty("items")[0].GetProperty("selectionMode").GetString());

        // Second upsert defaults to update (single field).
        var patch2 = await h.Client.PatchAsync("/api/customers/dictionaries/kind-settings", Body("{\"kind\":\"status\",\"sortOrder\":5}"));
        Assert.Equal(HttpStatusCode.OK, patch2.StatusCode);
        Assert.Equal(5, (await ReadJson(patch2)).GetProperty("sortOrder").GetInt32());
        Assert.Equal("multi", (await ReadJson(patch2)).GetProperty("selectionMode").GetString());
    }

    // ---- settings -----------------------------------------------------------------------------

    [Fact]
    public async Task AddressFormat_default_then_update()
    {
        await using var h = await BuildAsync();
        var get = await ReadJson(await h.Client.GetAsync("/api/customers/settings/address-format"));
        Assert.Equal("line_first", get.GetProperty("addressFormat").GetString());

        var put = await h.Client.PutAsync("/api/customers/settings/address-format", Body("{\"addressFormat\":\"street_first\"}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("street_first", (await ReadJson(put)).GetProperty("addressFormat").GetString());

        var after = await ReadJson(await h.Client.GetAsync("/api/customers/settings/address-format"));
        Assert.Equal("street_first", after.GetProperty("addressFormat").GetString());
    }

    [Fact]
    public async Task AddressFormat_invalid_returns_400()
    {
        await using var h = await BuildAsync();
        var put = await h.Client.PutAsync("/api/customers/settings/address-format", Body("{\"addressFormat\":\"nope\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task StuckThreshold_default_then_update_and_validation()
    {
        await using var h = await BuildAsync();
        var get = await ReadJson(await h.Client.GetAsync("/api/customers/settings/stuck-threshold"));
        Assert.Equal(14, get.GetProperty("stuckThresholdDays").GetInt32());

        var put = await h.Client.PutAsync("/api/customers/settings/stuck-threshold", Body("{\"stuckThresholdDays\":30}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(30, (await ReadJson(put)).GetProperty("stuckThresholdDays").GetInt32());

        var invalid = await h.Client.PutAsync("/api/customers/settings/stuck-threshold", Body("{\"stuckThresholdDays\":0}"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task DictionarySortModes_patch_affects_list_order()
    {
        await using var h = await BuildAsync();
        var empty = await ReadJson(await h.Client.GetAsync("/api/customers/settings/dictionary-sort-modes"));
        Assert.Equal(0, empty.GetProperty("dictionarySortModes").EnumerateObject().Count());

        await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"alpha\"}"));
        await h.Client.PostAsync("/api/customers/dictionaries/statuses", Body("{\"value\":\"beta\"}"));

        var patch = await h.Client.PatchAsync("/api/customers/settings/dictionary-sort-modes", Body("{\"dictionarySortModes\":{\"statuses\":\"label_desc\"}}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("label_desc", (await ReadJson(patch)).GetProperty("dictionarySortModes").GetProperty("statuses").GetString());

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/dictionaries/statuses"));
        Assert.Equal("label_desc", list.GetProperty("sortMode").GetString());
        var items = list.GetProperty("items");
        Assert.Equal("beta", items[0].GetProperty("value").GetString());
        Assert.Equal("alpha", items[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Settings_require_authentication()
    {
        await using var h = await BuildAsync(new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/customers/settings/address-format");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
