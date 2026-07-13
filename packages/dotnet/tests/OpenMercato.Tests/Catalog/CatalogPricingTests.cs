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
/// End-to-end HTTP tests for the catalog pricing spine: price-kinds, variants and prices. Each exercises
/// the index-backed list + command-handler write path (create → list → update → delete), the required-
/// field validation (400s), and the cross-entity scope wiring (variant inherits product scope; price
/// references a price kind).
/// </summary>
public class CatalogPricingTests
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
        // NB: compute the db name ONCE (not inside the options lambda) — the lambda runs per DbContext
        // instance, so an inline Guid would give every scope its own isolated in-memory store.
        var dbName = "catalog-pricing-" + Guid.NewGuid().ToString("N");
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
    public async Task PriceKind_create_list_update_delete_round_trip()
    {
        await using var h = await BuildAsync();

        var id = await PostIdAsync(h.Client, "/api/catalog/price-kinds",
            "{\"code\":\"retail\",\"title\":\"Retail\",\"displayMode\":\"including-tax\",\"isPromotion\":false}");
        await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"wholesale\",\"title\":\"Wholesale\"}");

        var list = await ReadJson(await h.Client.GetAsync("/api/catalog/price-kinds"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var retail = list.GetProperty("items").EnumerateArray().First(e => e.GetProperty("code").GetString() == "retail");
        Assert.Equal("including-tax", retail.GetProperty("display_mode").GetString());

        // Search by code/title.
        var searched = await ReadJson(await h.Client.GetAsync("/api/catalog/price-kinds?search=whole"));
        Assert.Equal(1, searched.GetProperty("total").GetInt32());

        var put = await h.Client.PutAsync("/api/catalog/price-kinds", Body($"{{\"id\":\"{id}\",\"title\":\"Retail (EU)\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await ReadJson(await h.Client.GetAsync($"/api/catalog/price-kinds?id={id}"));
        Assert.Equal("Retail (EU)", after.GetProperty("items").EnumerateArray().First().GetProperty("title").GetString());

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/price-kinds") { Content = Body($"{{\"id\":\"{id}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(1, (await ReadJson(await h.Client.GetAsync("/api/catalog/price-kinds"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task PriceKind_create_requires_code_and_title()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/price-kinds", Body("{\"title\":\"No Code\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/price-kinds", Body("{\"code\":\"ok\"}"))).StatusCode);
    }

    [Fact]
    public async Task Variant_create_inherits_product_scope_list_filter_update_delete()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Configurable Tee\"}");

        var variantId = await PostIdAsync(h.Client, "/api/catalog/variants",
            $"{{\"productId\":\"{productId}\",\"name\":\"Tee / M\",\"sku\":\"TEE-M\",\"isDefault\":true}}");

        // Index-backed list, snake_case; filter by productId.
        var list = await ReadJson(await h.Client.GetAsync($"/api/catalog/variants?productId={productId}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().First();
        Assert.Equal(productId, item.GetProperty("product_id").GetString());
        Assert.Equal("TEE-M", item.GetProperty("sku").GetString());
        Assert.True(item.GetProperty("is_default").GetBoolean());

        // Update SKU.
        var put = await h.Client.PutAsync("/api/catalog/variants", Body($"{{\"id\":\"{variantId}\",\"sku\":\"TEE-MED\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await ReadJson(await h.Client.GetAsync($"/api/catalog/variants?id={variantId}"));
        Assert.Equal("TEE-MED", after.GetProperty("items").EnumerateArray().First().GetProperty("sku").GetString());

        // Delete → list empty.
        await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/variants") { Content = Body($"{{\"id\":\"{variantId}\"}}") });
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync($"/api/catalog/variants?productId={productId}"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Variant_create_requires_productId()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/variants", Body("{\"sku\":\"ORPHAN\"}"))).StatusCode);
    }

    [Fact]
    public async Task Price_create_list_filter_update_delete_round_trip()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Priced Item\"}");
        var priceKindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");

        var priceId = await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{priceKindId}\",\"currencyCode\":\"usd\",\"unitPriceNet\":\"19.9900\",\"minQuantity\":1}}");

        // Filter by productId; currency is uppercased on write.
        var list = await ReadJson(await h.Client.GetAsync($"/api/catalog/prices?productId={productId}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().First();
        Assert.Equal("USD", item.GetProperty("currency_code").GetString());
        Assert.Equal(priceKindId, item.GetProperty("price_kind_id").GetString());

        // Update the net price.
        var put = await h.Client.PutAsync("/api/catalog/prices", Body($"{{\"id\":\"{priceId}\",\"unitPriceNet\":\"24.5000\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Delete hard-removes the row.
        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/prices") { Content = Body($"{{\"id\":\"{priceId}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync($"/api/catalog/prices?productId={productId}"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Price_create_requires_currency_and_price_kind()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/prices", Body("{\"currencyCode\":\"USD\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/prices", Body($"{{\"priceKindId\":\"{Guid.NewGuid()}\"}}"))).StatusCode);
    }
}
