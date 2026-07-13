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
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Catalog;

/// <summary>
/// End-to-end HTTP tests for the catalog products slice, wired with the real query_index projection +
/// the catalog base-row resolver + command handlers: create → index-backed list (snake_case fields) →
/// update (patch) → soft-delete round trip; categoryIds/tags association overlay; and the documented
/// quirks (title-required 400, delete-id-required 400).
/// </summary>
public class CatalogProductsTests
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

    private static async Task<Harness> BuildAsync(StubRequestContext? requestContext = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(),
            new EntitiesModule(),
            new QueryIndexModule(),
            new CatalogModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "catalog-tests-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => requestContext ?? new StubRequestContext());

        var app = builder.Build();
        new CatalogModule().MapRoutes(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateProductAsync(HttpClient client, string body)
    {
        var res = await client.PostAsync("/api/catalog/products", Body(body));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Product_create_list_update_delete_round_trip()
    {
        await using var h = await BuildAsync();

        var id = await CreateProductAsync(h.Client, "{\"title\":\"Alpha Widget\",\"sku\":\"ALPHA-1\",\"productType\":\"simple\"}");
        await CreateProductAsync(h.Client, "{\"title\":\"Beta Gadget\",\"sku\":\"BETA-2\"}");

        // Index-backed list: two products, snake_case DataQuery shape.
        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/products"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var first = list.GetProperty("items").EnumerateArray().First();
        Assert.True(first.TryGetProperty("product_type", out _));
        Assert.True(first.TryGetProperty("is_active", out _));

        // Free-text search narrows to one.
        var searched = await ReadJson(await h.Client.GetAsync("/api/catalog/products?search=Alpha"));
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
        Assert.Equal("Alpha Widget", searched.GetProperty("items").EnumerateArray().First().GetProperty("title").GetString());

        // Update (patch) the title → PUT returns { ok: true }.
        var put = await h.Client.PutAsync("/api/catalog/products", Body($"{{\"id\":\"{id}\",\"title\":\"Alpha Widget v2\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await ReadJson(put)).GetProperty("ok").GetBoolean());
        var afterUpdate = await ReadJson(await h.Client.GetAsync($"/api/catalog/products?id={id}"));
        Assert.Equal("Alpha Widget v2", afterUpdate.GetProperty("items").EnumerateArray().First().GetProperty("title").GetString());

        // Soft-delete → list drops to one.
        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/products")
        { Content = Body($"{{\"id\":\"{id}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var afterDelete = await ReadJson(await h.Client.GetAsync("/api/catalog/products"));
        Assert.Equal(1, afterDelete.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Product_create_syncs_categories_and_tags_overlaid_on_list()
    {
        await using var h = await BuildAsync();

        // Seed a category directly (categories route lands in a later slice).
        var categoryId = Guid.NewGuid();
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Set<CatalogProductCategory>().Add(new CatalogProductCategory
            {
                Id = categoryId, OrganizationId = Org, TenantId = Tenant, Name = "Tools",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var id = await CreateProductAsync(h.Client,
            $"{{\"title\":\"Hammer\",\"categoryIds\":[\"{categoryId}\"],\"tags\":[\"Featured\",\"On Sale\"]}}");

        // The association overlay runs on the list path (afterList hook), not the ?id= single fetch.
        var item = (await ReadJson(await h.Client.GetAsync("/api/catalog/products")))
            .GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == id);

        // Category overlay.
        var categoryIds = item.GetProperty("categoryIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(categoryId.ToString(), categoryIds);
        Assert.Equal("Tools", item.GetProperty("categories").EnumerateArray().First().GetProperty("name").GetString());

        // Tag overlay (labels), and the tags were created in the free pool with slugified slugs.
        var tags = item.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Featured", tags);
        Assert.Contains("On Sale", tags);
        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var slugs = await db.Set<CatalogProductTag>().Select(t => t.Slug).ToListAsync();
            Assert.Contains("featured", slugs);
            Assert.Contains("on-sale", slugs);
        }
    }

    [Fact]
    public async Task Product_create_without_title_returns_400()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/catalog/products", Body("{\"sku\":\"NO-TITLE\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Product_delete_without_id_returns_400()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/products")
        { Content = Body("{}") });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Product_list_requires_authentication()
    {
        await using var h = await BuildAsync(new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/catalog/products");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
