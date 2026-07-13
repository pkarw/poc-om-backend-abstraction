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
/// End-to-end HTTP tests for the remaining catalog route groups: product-unit-conversions and
/// option-schemas (index-backed CRUD), the read-only tags list, and the product-media validation stub.
/// </summary>
public class CatalogMiscRoutesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        public Task<CommandContext?> ResolveAsync(HttpContext http)
        {
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

    private static async Task<Harness> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(), new EntitiesModule(), new QueryIndexModule(), new CatalogModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "catalog-misc-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext());

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

    private static async Task<string> PostIdAsync(HttpClient client, string path, string body)
    {
        var res = await client.PostAsync(path, Body(body));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task UnitConversion_create_list_update_delete_round_trip()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Sold By Weight\",\"defaultUnit\":\"kg\"}");

        var id = await PostIdAsync(h.Client, "/api/catalog/product-unit-conversions",
            $"{{\"productId\":\"{productId}\",\"unitCode\":\"box\",\"toBaseFactor\":\"12.500000000000\"}}");

        var list = await ReadJson(await h.Client.GetAsync($"/api/catalog/product-unit-conversions?productId={productId}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().First();
        Assert.Equal("box", item.GetProperty("unit_code").GetString());
        Assert.Equal("box", item.GetProperty("unitCode").GetString()); // camelCase alias

        var put = await h.Client.PutAsync("/api/catalog/product-unit-conversions", Body($"{{\"id\":\"{id}\",\"sortOrder\":5}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/product-unit-conversions") { Content = Body($"{{\"id\":\"{id}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync($"/api/catalog/product-unit-conversions?productId={productId}"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task UnitConversion_create_validation()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"P\"}");
        // missing unitCode
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/product-unit-conversions", Body($"{{\"productId\":\"{productId}\",\"toBaseFactor\":\"1\"}}"))).StatusCode);
        // non-positive factor
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/product-unit-conversions", Body($"{{\"productId\":\"{productId}\",\"unitCode\":\"x\",\"toBaseFactor\":\"0\"}}"))).StatusCode);
        // unknown product → 404
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.PostAsync("/api/catalog/product-unit-conversions", Body($"{{\"productId\":\"{Guid.NewGuid()}\",\"unitCode\":\"x\",\"toBaseFactor\":\"1\"}}"))).StatusCode);
    }

    [Fact]
    public async Task OptionSchema_create_list_update_delete_round_trip()
    {
        await using var h = await BuildAsync();
        var id = await PostIdAsync(h.Client, "/api/catalog/option-schemas",
            "{\"name\":\"Shirt Options\",\"code\":\"shirt\",\"schema\":{\"options\":[{\"code\":\"size\",\"label\":\"Size\",\"inputType\":\"select\"}]}}");

        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/option-schemas"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().First();
        Assert.Equal("Shirt Options", item.GetProperty("name").GetString());
        // schema echoed as a parsed object.
        Assert.Equal(1, item.GetProperty("schema").GetProperty("options").GetArrayLength());

        var put = await h.Client.PutAsync("/api/catalog/option-schemas", Body($"{{\"id\":\"{id}\",\"name\":\"Shirt Options v2\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await ReadJson(await h.Client.GetAsync($"/api/catalog/option-schemas?id={id}"));
        Assert.Equal("Shirt Options v2", after.GetProperty("items").EnumerateArray().First().GetProperty("name").GetString());

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/option-schemas") { Content = Body($"{{\"id\":\"{id}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync("/api/catalog/option-schemas"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OptionSchema_create_requires_name_and_schema()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/option-schemas", Body("{\"schema\":{\"options\":[]}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/option-schemas", Body("{\"name\":\"No Schema\"}"))).StatusCode);
    }

    [Fact]
    public async Task Tags_list_reflects_product_created_tags()
    {
        await using var h = await BuildAsync();
        await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Tagged\",\"tags\":[\"Featured\",\"On Sale\"]}");

        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/tags"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var labels = list.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("label").GetString()).ToList();
        Assert.Contains("Featured", labels);
        Assert.Contains("On Sale", labels);

        // label search.
        var searched = await ReadJson(await h.Client.GetAsync("/api/catalog/tags?search=featured"));
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ProductMedia_validates_productId_and_scope()
    {
        await using var h = await BuildAsync();
        // missing productId → 400
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.GetAsync("/api/catalog/product-media")).StatusCode);
        // unknown product → 404
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync($"/api/catalog/product-media?productId={Guid.NewGuid()}")).StatusCode);
        // in-scope product → empty items (attachments not ported)
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Has No Media\"}");
        var ok = await ReadJson(await h.Client.GetAsync($"/api/catalog/product-media?productId={productId}"));
        Assert.Equal(0, ok.GetProperty("items").GetArrayLength());
    }
}
