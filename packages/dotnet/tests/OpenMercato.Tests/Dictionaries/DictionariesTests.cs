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
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dictionaries;
using OpenMercato.Modules.Dictionaries.Data;
using Xunit;

namespace OpenMercato.Tests.Dictionaries;

/// <summary>
/// HTTP-level tests for the dictionaries module: the bespoke <c>{ items }</c> list envelopes
/// (dictionaries + entries), command-bus-backed create/update/delete, entry ordering by
/// <c>entry_sort_mode</c>, duplicate detection (409), reorder + set-default operations, currency
/// protection, and the auth guards.
/// </summary>
public class DictionariesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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
                TenantId = Tenant,
                OrganizationId = Org,
                UserId = Guid.NewGuid(),
                OrganizationIds = new[] { Org },
                AllowedOrganizationIds = new[] { Org },
                Headers = headers,
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
            new DictionariesModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "dict-tests-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddScoped<ICrudRequestContext>(_ => requestContext ?? new StubRequestContext());

        var app = builder.Build();
        // Only map the dictionaries routes (Directory is in the registry solely so its Organization
        // entity is in the model for the org-inheritance read scope; its routes need host-only services).
        new DictionariesModule().MapRoutes(app);

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

    private static Dictionary NewDictionary(string key, string name, bool active = true, string sortMode = "label_asc")
    {
        var now = DateTimeOffset.UtcNow;
        return new Dictionary
        {
            Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, Key = key, Name = name,
            IsActive = active, ManagerVisibility = "default", EntrySortMode = sortMode, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static DictionaryEntry NewEntry(Guid dictId, string value, string label, int position = 0, bool isDefault = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new DictionaryEntry
        {
            Id = Guid.NewGuid(), DictionaryId = dictId, OrganizationId = Org, TenantId = Tenant,
            Value = value, NormalizedValue = value.Trim().ToLowerInvariant(), Label = label,
            Position = position, IsDefault = isDefault, CreatedAt = now, UpdatedAt = now,
        };
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    // ---- dictionary list / create -------------------------------------------------------------

    [Fact]
    public async Task List_returns_items_ordered_by_name_excluding_inactive()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Dictionary>().Add(NewDictionary("b", "Bravo"));
            db.Set<Dictionary>().Add(NewDictionary("a", "Alpha"));
            db.Set<Dictionary>().Add(NewDictionary("c", "Charlie", active: false));
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/dictionaries"));
        var names = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "Alpha", "Bravo" }, names);
        // No pagination envelope keys — just { items }.
        Assert.Equal(new[] { "items" }, body.EnumerateObject().Select(p => p.Name).ToArray());

        var all = await ReadJson(await h.Client.GetAsync("/api/dictionaries?includeInactive=true"));
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Create_dictionary_returns_201_and_rejects_duplicate_key()
    {
        await using var h = await BuildAsync();

        var res = await h.Client.PostAsync("/api/dictionaries", JsonBody("{\"key\":\"Priorities\",\"name\":\"Priorities\"}"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("priorities", body.GetProperty("key").GetString()); // lowercased
        Assert.Equal("label_asc", body.GetProperty("entrySortMode").GetString());

        var dup = await h.Client.PostAsync("/api/dictionaries", JsonBody("{\"key\":\"priorities\",\"name\":\"Again\"}"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Create_dictionary_rejects_invalid_key()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/dictionaries", JsonBody("{\"key\":\"Has Space\",\"name\":\"X\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Get_patch_delete_dictionary_lifecycle()
    {
        Dictionary d = NewDictionary("status", "Status");
        await using var h = await BuildAsync(seed: db => db.Set<Dictionary>().Add(d));

        var got = await ReadJson(await h.Client.GetAsync($"/api/dictionaries/{d.Id}"));
        Assert.Equal("Status", got.GetProperty("name").GetString());
        Assert.False(got.GetProperty("isInherited").GetBoolean());

        var patch = await h.Client.PatchAsync($"/api/dictionaries/{d.Id}", JsonBody("{\"name\":\"Renamed\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Renamed", (await ReadJson(patch)).GetProperty("name").GetString());

        var del = await h.Client.DeleteAsync($"/api/dictionaries/{d.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.True((await ReadJson(del)).GetProperty("ok").GetBoolean());

        // Soft-deleted → gone from the list.
        var list = await ReadJson(await h.Client.GetAsync("/api/dictionaries"));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    // ---- entries ------------------------------------------------------------------------------

    [Fact]
    public async Task Entries_list_sorted_by_value_desc_mode()
    {
        Dictionary d = NewDictionary("colors", "Colors", sortMode: "value_desc");
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Dictionary>().Add(d);
            db.Set<DictionaryEntry>().Add(NewEntry(d.Id, "amber", "Amber"));
            db.Set<DictionaryEntry>().Add(NewEntry(d.Id, "cyan", "Cyan"));
            db.Set<DictionaryEntry>().Add(NewEntry(d.Id, "blue", "Blue"));
        });

        var body = await ReadJson(await h.Client.GetAsync($"/api/dictionaries/{d.Id}/entries"));
        var values = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("value").GetString()).ToArray();
        Assert.Equal(new[] { "cyan", "blue", "amber" }, values);
    }

    [Fact]
    public async Task Create_entry_via_command_returns_201_with_operation_header_and_rejects_duplicate()
    {
        Dictionary d = NewDictionary("status", "Status");
        await using var h = await BuildAsync(seed: db => db.Set<Dictionary>().Add(d));

        var res = await h.Client.PostAsync($"/api/dictionaries/{d.Id}/entries", JsonBody("{\"value\":\"Active\",\"color\":\"#33AA55\"}"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Active", body.GetProperty("value").GetString());
        Assert.Equal("Active", body.GetProperty("label").GetString());   // label defaults to value
        Assert.Equal("#33aa55", body.GetProperty("color").GetString());  // sanitized lowercase
        Assert.True(res.Headers.TryGetValues("x-om-operation", out var vals));
        Assert.StartsWith("omop:", vals!.First());

        // Duplicate normalized value → 409.
        var dup = await h.Client.PostAsync($"/api/dictionaries/{d.Id}/entries", JsonBody("{\"value\":\"active\"}"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Update_and_delete_entry()
    {
        Dictionary d = NewDictionary("status", "Status");
        DictionaryEntry e = NewEntry(d.Id, "active", "Active");
        await using var h = await BuildAsync(seed: db => { db.Set<Dictionary>().Add(d); db.Set<DictionaryEntry>().Add(e); });

        var patch = await h.Client.PatchAsync($"/api/dictionaries/{d.Id}/entries/{e.Id}", JsonBody("{\"label\":\"Currently Active\"}"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Currently Active", (await ReadJson(patch)).GetProperty("label").GetString());

        var del = await h.Client.DeleteAsync($"/api/dictionaries/{d.Id}/entries/{e.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var list = await ReadJson(await h.Client.GetAsync($"/api/dictionaries/{d.Id}/entries"));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Reorder_entries_updates_positions()
    {
        Dictionary d = NewDictionary("status", "Status", sortMode: "created_at_asc");
        DictionaryEntry a = NewEntry(d.Id, "a", "A", position: 0);
        DictionaryEntry b = NewEntry(d.Id, "b", "B", position: 1);
        await using var h = await BuildAsync(seed: db => { db.Set<Dictionary>().Add(d); db.Set<DictionaryEntry>().AddRange(a, b); });

        var res = await h.Client.PostAsync($"/api/dictionaries/{d.Id}/entries/reorder",
            JsonBody($"{{\"entries\":[{{\"id\":\"{a.Id}\",\"position\":5}},{{\"id\":\"{b.Id}\",\"position\":2}}]}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(5, (await db.Set<DictionaryEntry>().FirstAsync(x => x.Id == a.Id)).Position);
        Assert.Equal(2, (await db.Set<DictionaryEntry>().FirstAsync(x => x.Id == b.Id)).Position);
    }

    [Fact]
    public async Task Set_default_moves_default_to_target_entry()
    {
        Dictionary d = NewDictionary("status", "Status");
        DictionaryEntry a = NewEntry(d.Id, "a", "A", isDefault: true);
        DictionaryEntry b = NewEntry(d.Id, "b", "B");
        await using var h = await BuildAsync(seed: db => { db.Set<Dictionary>().Add(d); db.Set<DictionaryEntry>().AddRange(a, b); });

        var res = await h.Client.PostAsync($"/api/dictionaries/{d.Id}/entries/set-default", JsonBody($"{{\"entryId\":\"{b.Id}\"}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False((await db.Set<DictionaryEntry>().FirstAsync(x => x.Id == a.Id)).IsDefault);
        Assert.True((await db.Set<DictionaryEntry>().FirstAsync(x => x.Id == b.Id)).IsDefault);
    }

    // ---- protection + auth --------------------------------------------------------------------

    [Fact]
    public async Task Currency_dictionary_cannot_be_deleted()
    {
        Dictionary d = NewDictionary("currency", "Currencies");
        await using var h = await BuildAsync(seed: db => db.Set<Dictionary>().Add(d));

        var res = await h.Client.DeleteAsync($"/api/dictionaries/{d.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_returns_401()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/dictionaries");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Missing_feature_returns_403()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(hasFeatures: false));
        var res = await h.Client.PostAsync("/api/dictionaries", JsonBody("{\"key\":\"x\",\"name\":\"X\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
