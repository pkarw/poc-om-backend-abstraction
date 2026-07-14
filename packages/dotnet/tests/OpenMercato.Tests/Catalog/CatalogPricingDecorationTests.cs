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
/// End-to-end tests for the products list pricing decoration (upstream decorateProductsAfterList →
/// item.pricing): base price resolution, variant-price specificity winning over a product price, and
/// channel-scoped applicability against the ?channelId= context.
/// </summary>
public class CatalogPricingDecorationTests
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
        var dbName = "catalog-pricingdec-" + Guid.NewGuid().ToString("N");
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
    private static async Task<JsonElement> ProductItem(HttpClient client, string productId)
    {
        var list = await ReadJson(await client.GetAsync("/api/catalog/products"));
        return list.GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == productId);
    }

    [Fact]
    public async Task Product_pricing_resolves_base_price()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Widget\"}");
        var kindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"19.9900\"}}");

        var pricing = (await ProductItem(h.Client, productId)).GetProperty("pricing");
        Assert.Equal("USD", pricing.GetProperty("currency_code").GetString());
        Assert.Equal(19.99m, pricing.GetProperty("unit_price_net").GetDecimal());
        Assert.Equal("retail", pricing.GetProperty("price_kind_code").GetString());
        Assert.False(pricing.GetProperty("scope").TryGetProperty("variant_id", out var vid) && vid.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Variant_price_wins_over_product_price()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Configurable\"}");
        var variantId = await PostIdAsync(h.Client, "/api/catalog/variants", $"{{\"productId\":\"{productId}\",\"sku\":\"V1\"}}");
        var kindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"20.0000\"}}");
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"variantId\":\"{variantId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"15.0000\"}}");

        var pricing = (await ProductItem(h.Client, productId)).GetProperty("pricing");
        // The variant-scoped price is more specific (scores higher) → it wins.
        Assert.Equal(15.00m, pricing.GetProperty("unit_price_net").GetDecimal());
        Assert.Equal(variantId, pricing.GetProperty("scope").GetProperty("variant_id").GetString());
    }

    [Fact]
    public async Task Channel_scoped_price_only_applies_with_matching_channel()
    {
        await using var h = await BuildAsync();
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"Channelled\"}");
        var kindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");
        var channelId = Guid.NewGuid();
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"12.0000\",\"channelId\":\"{channelId}\"}}");

        // Without a channel context the channel-scoped price is not applicable → no pricing.
        var noChannel = (await ReadJson(await h.Client.GetAsync("/api/catalog/products")))
            .GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == productId);
        Assert.Equal(JsonValueKind.Null, noChannel.GetProperty("pricing").ValueKind);

        // With the matching channel it resolves.
        var withChannel = (await ReadJson(await h.Client.GetAsync($"/api/catalog/products?channelId={channelId}")))
            .GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == productId);
        Assert.Equal(12.00m, withChannel.GetProperty("pricing").GetProperty("unit_price_net").GetDecimal());
        Assert.Equal(channelId.ToString(), withChannel.GetProperty("pricing").GetProperty("scope").GetProperty("channel_id").GetString());
    }

    [Fact]
    public async Task QuantityUnit_conversion_reaches_a_bulk_tier()
    {
        await using var h = await BuildAsync();
        // A product sold by kg, with a "box" conversion of 12.5 kg per box.
        var productId = await PostIdAsync(h.Client, "/api/catalog/products", "{\"title\":\"By Weight\",\"defaultUnit\":\"kg\"}");
        await PostIdAsync(h.Client, "/api/catalog/product-unit-conversions",
            $"{{\"productId\":\"{productId}\",\"unitCode\":\"box\",\"toBaseFactor\":\"12.5\"}}");
        var kindId = await PostIdAsync(h.Client, "/api/catalog/price-kinds", "{\"code\":\"retail\",\"title\":\"Retail\"}");
        // Base tier (min 1) and a bulk tier (min 10).
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"10.0000\",\"minQuantity\":1}}");
        await PostIdAsync(h.Client, "/api/catalog/prices",
            $"{{\"productId\":\"{productId}\",\"priceKindId\":\"{kindId}\",\"currencyCode\":\"USD\",\"unitPriceNet\":\"8.0000\",\"minQuantity\":10}}");

        // qty=1 kg → only the base tier applies.
        var baseTier = (await ReadJson(await h.Client.GetAsync("/api/catalog/products?quantity=1")))
            .GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == productId);
        Assert.Equal(10.00m, baseTier.GetProperty("pricing").GetProperty("unit_price_net").GetDecimal());

        // qty=1 box = 12.5 kg → the bulk tier (min 10) now applies and wins.
        var bulkTier = (await ReadJson(await h.Client.GetAsync("/api/catalog/products?quantity=1&quantityUnit=box")))
            .GetProperty("items").EnumerateArray().First(e => e.GetProperty("id").GetString() == productId);
        Assert.Equal(8.00m, bulkTier.GetProperty("pricing").GetProperty("unit_price_net").GetDecimal());
    }
}
