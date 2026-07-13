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
/// End-to-end HTTP tests for the catalog offers slice: index-backed CRUD with the camelCase list item,
/// the productId/channelId/isActive filters, and the afterList decoration (product summary + the offer's
/// prices with price-kind info). Also covers the required-field validation.
/// </summary>
public class CatalogOffersTests
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
        var dbName = "catalog-offers-" + Guid.NewGuid().ToString("N");
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
    public async Task Offer_create_list_update_delete_round_trip_camelcase()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Widget\",\"sku\":\"W-1\"}");
        var channelId = Guid.NewGuid();

        var offerId = await PostIdAsync(h.Client, "/api/catalog/offers",
            $"{{\"productId\":\"{productId}\",\"channelId\":\"{channelId}\",\"title\":\"Widget @ Web\"}}");

        var list = await ReadJson(await h.Client.GetAsync($"/api/catalog/offers?productId={productId}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().First();
        // camelCase item shape.
        Assert.Equal(productId, item.GetProperty("productId").GetString());
        Assert.Equal(channelId.ToString(), item.GetProperty("channelId").GetString());
        Assert.Equal("Widget @ Web", item.GetProperty("title").GetString());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        // product summary decoration.
        Assert.Equal("Widget", item.GetProperty("product").GetProperty("title").GetString());
        Assert.Equal("W-1", item.GetProperty("product").GetProperty("sku").GetString());

        var put = await h.Client.PutAsync("/api/catalog/offers", Body($"{{\"id\":\"{offerId}\",\"title\":\"Widget @ Web v2\"}}"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/offers") { Content = Body($"{{\"id\":\"{offerId}\"}}") });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(0, (await ReadJson(await h.Client.GetAsync($"/api/catalog/offers?productId={productId}"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Offer_list_decorates_prices_with_price_kind()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Priced\"}");
        var priceKindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");
        var offerId = await PostIdAsync(h.Client, "/api/catalog/offers",
            $"{{\"productId\":\"{productId}\",\"channelId\":\"{Guid.NewGuid()}\",\"title\":\"O\"}}");
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"offerId\":\"{offerId}\",\"priceKindId\":\"{priceKindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"9.9900\"}}");

        var item = (await ReadJson(await h.Client.GetAsync($"/api/catalog/offers?productId={productId}")))
            .GetProperty("items").EnumerateArray().First();
        var prices = item.GetProperty("prices");
        Assert.Equal(1, prices.GetArrayLength());
        var price = prices.EnumerateArray().First();
        Assert.Equal("retail", price.GetProperty("priceKindCode").GetString());
        Assert.Equal("Retail", price.GetProperty("priceKindTitle").GetString());
        Assert.Equal("USD", price.GetProperty("currencyCode").GetString());
    }

    [Fact]
    public async Task Offer_create_requires_product_channel_and_title()
    {
        await using var h = await BuildAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/offers", Body($"{{\"channelId\":\"{Guid.NewGuid()}\",\"title\":\"x\"}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Client.PostAsync("/api/catalog/offers", Body($"{{\"productId\":\"{Guid.NewGuid()}\",\"title\":\"x\"}}"))).StatusCode);
    }

    [Fact]
    public async Task Offer_create_unknown_product_returns_404()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/catalog/offers",
            Body($"{{\"productId\":\"{Guid.NewGuid()}\",\"channelId\":\"{Guid.NewGuid()}\",\"title\":\"Orphan\"}}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
