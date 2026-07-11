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
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using OpenMercato.Modules.QueryIndex.Data;
using Xunit;

namespace OpenMercato.Tests.Entities;

/// <summary>Tests for GET /api/entities/relations/options (relation-picker options over entity_indexes).</summary>
public class RelationsOptionsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private const string Entity = "customers:customer_person_profile";

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

    private static async Task<Harness> BuildAsync(Action<AppDbContext> seed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[] { new EntitiesModule(), new QueryIndexModule() });
        builder.Services.AddSingleton(registry);
        var dbName = "relations-options-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext());

        var app = builder.Build();
        new EntitiesModule().MapRoutes(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }
        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static void Row(AppDbContext db, string id, string doc) => db.Set<EntityIndexRow>().Add(new EntityIndexRow
    {
        Id = Guid.NewGuid(), EntityType = Entity, EntityId = id, TenantId = Tenant, OrganizationId = Org,
        Doc = doc, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    });

    private static async Task<JsonElement> Get(HttpClient c, string url) =>
        JsonDocument.Parse(await (await c.GetAsync(url)).Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Empty_entity_id_returns_empty_items()
    {
        await using var h = await BuildAsync(_ => { });
        var json = await Get(h.Client, "/api/entities/relations/options");
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Ids_filter_returns_value_and_label_without_routeContext()
    {
        await using var h = await BuildAsync(db =>
        {
            Row(db, "rec-1", "{\"id\":\"rec-1\",\"title\":\"Alpha\"}");
            Row(db, "rec-2", "{\"id\":\"rec-2\",\"title\":\"Beta\"}");
            Row(db, "rec-3", "{\"id\":\"rec-3\",\"title\":\"Gamma\"}");
        });
        var json = await Get(h.Client, "/api/entities/relations/options?entityId=" + Entity + "&labelField=title&ids=rec-1,rec-2");
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var alpha = items.Single(i => i.GetProperty("value").GetString() == "rec-1");
        Assert.Equal("Alpha", alpha.GetProperty("label").GetString());
        Assert.False(alpha.TryGetProperty("routeContext", out _)); // omitted when empty
    }

    [Fact]
    public async Task RouteContext_only_includes_present_allowlisted_fields()
    {
        await using var h = await BuildAsync(db =>
            Row(db, "rec-1", "{\"id\":\"rec-1\",\"title\":\"Ada\",\"kind\":\"person\"}"));
        // request kind + email; doc only has kind → routeContext = {kind}, email dropped.
        var json = await Get(h.Client, "/api/entities/relations/options?entityId=" + Entity + "&labelField=title&ids=rec-1&routeContextFields=kind,email");
        var item = json.GetProperty("items").EnumerateArray().Single();
        var rc = item.GetProperty("routeContext");
        Assert.Equal("person", rc.GetProperty("kind").GetString());
        Assert.False(rc.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Q_substring_filters_on_label_field()
    {
        await using var h = await BuildAsync(db =>
        {
            Row(db, "rec-1", "{\"id\":\"rec-1\",\"title\":\"Ada Lovelace\"}");
            Row(db, "rec-2", "{\"id\":\"rec-2\",\"title\":\"Alan Turing\"}");
        });
        var json = await Get(h.Client, "/api/entities/relations/options?entityId=" + Entity + "&labelField=title&q=lovel");
        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("rec-1", item.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Label_field_falls_back_to_present_candidate_column()
    {
        await using var h = await BuildAsync(db =>
            Row(db, "rec-1", "{\"id\":\"rec-1\",\"name\":\"Widget\"}"));
        // no labelField param → probes name/title/code/email → 'name' present.
        var json = await Get(h.Client, "/api/entities/relations/options?entityId=" + Entity + "&ids=rec-1");
        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Widget", item.GetProperty("label").GetString());
    }
}
