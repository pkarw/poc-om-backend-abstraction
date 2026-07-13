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
using OpenMercato.Modules.Catalog;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Catalog;

/// <summary>
/// End-to-end HTTP tests for the catalog categories slice: the hand-written hierarchy GET (manage +
/// tree views) and the materialized-path rebuild driven by the create/update/delete commands. Covers
/// parent/child depth + pathLabel + child/descendant counts, re-parenting, soft-delete, and the
/// validation quirks (name-required, parent-not-found, slug-uniqueness).
/// </summary>
public class CatalogCategoriesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _authenticated;
        public StubRequestContext(bool authenticated = true) { _authenticated = authenticated; }
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
        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(true);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(StubRequestContext? rc = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(), new EntitiesModule(), new QueryIndexModule(), new CatalogModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "catalog-categories-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => rc ?? new StubRequestContext());

        var app = builder.Build();
        new CatalogModule().MapRoutes(app);
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateAsync(HttpClient client, string body)
    {
        var res = await client.PostAsync("/api/catalog/categories", Body(body));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("id").GetString()!;
    }

    private static JsonElement ItemById(JsonElement list, string id) =>
        list.GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Category_hierarchy_manage_view_computes_paths_and_counts()
    {
        await using var h = await BuildAsync();
        var parentId = await CreateAsync(h.Client, "{\"name\":\"Apparel\",\"slug\":\"apparel\"}");
        var childId = await CreateAsync(h.Client, $"{{\"name\":\"Shirts\",\"parentId\":\"{parentId}\"}}");
        var grandChildId = await CreateAsync(h.Client, $"{{\"name\":\"T-Shirts\",\"parentId\":\"{childId}\"}}");

        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/categories"));
        Assert.Equal(3, list.GetProperty("total").GetInt32());

        var parent = ItemById(list, parentId);
        Assert.Equal(0, parent.GetProperty("depth").GetInt32());
        Assert.Equal(1, parent.GetProperty("childCount").GetInt32());
        Assert.Equal(2, parent.GetProperty("descendantCount").GetInt32());
        Assert.Equal("Apparel", parent.GetProperty("pathLabel").GetString());

        var child = ItemById(list, childId);
        Assert.Equal(1, child.GetProperty("depth").GetInt32());
        Assert.Equal("Apparel", child.GetProperty("parentName").GetString());
        Assert.Equal("Apparel / Shirts", child.GetProperty("pathLabel").GetString());

        var grand = ItemById(list, grandChildId);
        Assert.Equal(2, grand.GetProperty("depth").GetInt32());
        Assert.Equal("Apparel / Shirts / T-Shirts", grand.GetProperty("pathLabel").GetString());
        Assert.Equal($"{parentId}/{childId}/{grandChildId}", grand.GetProperty("treePath").GetString());
    }

    [Fact]
    public async Task Category_tree_view_returns_nested_roots()
    {
        await using var h = await BuildAsync();
        var parentId = await CreateAsync(h.Client, "{\"name\":\"Electronics\"}");
        await CreateAsync(h.Client, $"{{\"name\":\"Phones\",\"parentId\":\"{parentId}\"}}");

        var tree = await ReadJson(await h.Client.GetAsync("/api/catalog/categories?view=tree"));
        var roots = tree.GetProperty("items");
        Assert.Equal(1, roots.GetArrayLength());
        var root = roots.EnumerateArray().First();
        Assert.Equal("Electronics", root.GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("children").GetArrayLength());
        Assert.Equal("Phones", root.GetProperty("children").EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task Category_reparent_recomputes_hierarchy()
    {
        await using var h = await BuildAsync();
        var a = await CreateAsync(h.Client, "{\"name\":\"Alpha\"}");
        var b = await CreateAsync(h.Client, "{\"name\":\"Beta\"}");
        var child = await CreateAsync(h.Client, $"{{\"name\":\"Gamma\",\"parentId\":\"{a}\"}}");

        // Move Gamma from Alpha to Beta.
        var put = await h.Client.PutAsync("/api/catalog/categories", Body($"{{\"id\":\"{child}\",\"parentId\":\"{b}\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/categories"));
        var gamma = ItemById(list, child);
        Assert.Equal(b, gamma.GetProperty("parentId").GetString());
        Assert.Equal("Beta / Gamma", gamma.GetProperty("pathLabel").GetString());
        Assert.Equal(0, ItemById(list, a).GetProperty("childCount").GetInt32());
        Assert.Equal(1, ItemById(list, b).GetProperty("childCount").GetInt32());
    }

    [Fact]
    public async Task Category_delete_removes_from_hierarchy()
    {
        await using var h = await BuildAsync();
        var id = await CreateAsync(h.Client, "{\"name\":\"Temp\"}");
        Assert.Equal(1, (await ReadJson(await h.Client.GetAsync("/api/catalog/categories"))).GetProperty("total").GetInt32());

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/categories") { Content = Body($"{{\"id\":\"{id}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync("/api/catalog/categories"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Category_status_and_search_filters()
    {
        await using var h = await BuildAsync();
        await CreateAsync(h.Client, "{\"name\":\"Active One\",\"isActive\":true}");
        await CreateAsync(h.Client, "{\"name\":\"Hidden Two\",\"isActive\":false}");

        Assert.Equal(1, (await ReadJson(await h.Client.GetAsync("/api/catalog/categories?status=active"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ReadJson(await h.Client.GetAsync("/api/catalog/categories?status=inactive"))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await ReadJson(await h.Client.GetAsync("/api/catalog/categories?search=hidden"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Category_create_validation_quirks()
    {
        await using var h = await BuildAsync();
        // name required
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/categories", Body("{\"slug\":\"x\"}"))).StatusCode);
        // parent not found → 400
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/categories", Body($"{{\"name\":\"Orphan\",\"parentId\":\"{Guid.NewGuid()}\"}}"))).StatusCode);
        // slug uniqueness
        await CreateAsync(h.Client, "{\"name\":\"First\",\"slug\":\"dup\"}");
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/categories", Body("{\"name\":\"Second\",\"slug\":\"dup\"}"))).StatusCode);
    }

    [Fact]
    public async Task Category_list_requires_authentication()
    {
        await using var h = await BuildAsync(new StubRequestContext(authenticated: false));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client.GetAsync("/api/catalog/categories")).StatusCode);
    }
}
