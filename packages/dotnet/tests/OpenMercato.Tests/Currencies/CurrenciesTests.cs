using System.Net;
using System.Net.Http.Json;
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
using OpenMercato.Modules.Currencies;
using OpenMercato.Modules.Currencies.Data;
using OpenMercato.Modules.Currencies.Seeding;
using OpenMercato.Modules.Currencies.Services;
using Xunit;

namespace OpenMercato.Tests.Currencies;

/// <summary>
/// Tests for the ported currencies module: the default-currency seeder (idempotent, USD base), the
/// ExchangeRateService ConvertToBase-style conversion (incl. day-fallback + degraded paths), and the
/// admin CRUD surface driven through the CRUD factory + command bus (currencies + exchange-rates:
/// duplicate/base/active-rate guards, currency-existence checks, list filters, events + soft delete).
/// </summary>
public class CurrenciesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- Recording seams / stub auth ---------------------------------------------------------

    private sealed class RecordingEventBus : IEventBus
    {
        public List<string> Published { get; } = new();
        public Task PublishAsync(string eventName, object payload, CancellationToken ct = default)
        { Published.Add(eventName); return Task.CompletedTask; }
    }

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly IReadOnlyList<Guid>? _orgIds;
        private readonly bool _authenticated;
        public StubRequestContext(IReadOnlyList<Guid>? orgIds = null, bool authenticated = true)
        { _orgIds = orgIds; _authenticated = authenticated; }

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
                OrganizationIds = _orgIds,
                Headers = headers,
            });
        }

        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(true);
    }

    private sealed record Harness(WebApplication App, HttpClient Client, RecordingEventBus Events) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(Action<AppDbContext>? seed = null, StubRequestContext? requestContext = null)
    {
        var events = new RecordingEventBus();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[] { new AuditLogsModule(), new CurrenciesModule() });
        builder.Services.AddSingleton(registry);
        var dbName = "cur-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddOpenMercatoCrud();
        registry.ConfigureServices(builder.Services);
        builder.Services.AddScoped<ICrudRequestContext>(_ => requestContext ?? new StubRequestContext());
        builder.Services.AddSingleton<IEventBus>(events);

        var app = builder.Build();
        registry.MapRoutes(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            seed?.Invoke(db);
            db.SaveChanges();
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient(), events);
    }

    private static Currency NewCurrency(string code, string name, bool isBase = false, bool active = true) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        OrganizationId = Org,
        Code = code,
        Name = name,
        IsBase = isBase,
        IsActive = active,
        DecimalPlaces = 2,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    // ---- Seeder ------------------------------------------------------------------------------

    [Fact]
    public async Task Seeder_seeds_ten_default_currencies_with_usd_base_idempotently()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("seed-" + Guid.NewGuid().ToString("N")).Options;
        var registry = new ModuleRegistry(new IModule[] { new CurrenciesModule() });
        await using var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();

        var first = await CurrenciesSeeder.SeedExampleCurrenciesAsync(db, Tenant, Org);
        Assert.True(first);

        var all = await db.Set<Currency>().Where(c => c.TenantId == Tenant && c.OrganizationId == Org).ToListAsync();
        Assert.Equal(10, all.Count);
        var baseCurrencies = all.Where(c => c.IsBase).ToList();
        Assert.Single(baseCurrencies);
        Assert.Equal("USD", baseCurrencies[0].Code);
        Assert.Contains(all, c => c.Code == "PLN" && c.ThousandsSeparator == " ");
        Assert.Contains(all, c => c.Code == "JPY" && c.DecimalPlaces == 0);

        // Second run is a no-op (idempotent) and does not duplicate rows.
        var second = await CurrenciesSeeder.SeedExampleCurrenciesAsync(db, Tenant, Org);
        Assert.False(second);
        Assert.Equal(10, await db.Set<Currency>().CountAsync(c => c.TenantId == Tenant && c.OrganizationId == Org));
    }

    // ---- ConvertToBase (the API customers call) ----------------------------------------------

    private static ExchangeRateService NewService(AppDbContext db) => new(db);

    private static void SeedRate(AppDbContext db, string from, string to, decimal rate, DateTimeOffset dateMidnightUtc)
    {
        db.Set<ExchangeRate>().Add(new ExchangeRate
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            OrganizationId = Org,
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = rate,
            Date = dateMidnightUtc,
            Source = "test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static DateTimeOffset TodayUtcMidnight()
    {
        var n = DateTimeOffset.UtcNow;
        return new DateTimeOffset(n.Year, n.Month, n.Day, 0, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task ConvertToBase_multiplies_by_the_rate_to_the_base_currency()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("conv-" + Guid.NewGuid().ToString("N")).Options;
        var registry = new ModuleRegistry(new IModule[] { new CurrenciesModule() });
        await using var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
        db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
        SeedRate(db, "EUR", "USD", 1.1m, TodayUtcMidnight());
        await db.SaveChangesAsync();

        var svc = NewService(db);
        var result = await svc.ConvertToBaseAsync(100m, "EUR", DateTimeOffset.UtcNow, new CurrencyScope(Tenant, Org));

        Assert.True(result.Converted);
        Assert.Equal("USD", result.BaseCurrencyCode);
        Assert.Equal(1.1m, result.Rate);
        Assert.Equal(110m, result.Amount);
    }

    [Fact]
    public async Task ConvertToBase_returns_amount_unchanged_when_source_is_base()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("conv2-" + Guid.NewGuid().ToString("N")).Options;
        var registry = new ModuleRegistry(new IModule[] { new CurrenciesModule() });
        await using var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
        await db.SaveChangesAsync();

        var result = await NewService(db).ConvertToBaseAsync(42m, "USD", DateTimeOffset.UtcNow, new CurrencyScope(Tenant, Org));
        Assert.True(result.Converted);
        Assert.Equal(1m, result.Rate);
        Assert.Equal(42m, result.Amount);
    }

    [Fact]
    public async Task ConvertToBase_reports_not_converted_when_no_rate_or_no_base()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("conv3-" + Guid.NewGuid().ToString("N")).Options;
        var registry = new ModuleRegistry(new IModule[] { new CurrenciesModule() });
        await using var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
        db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
        await db.SaveChangesAsync();

        // No rate seeded → not converted, amount unchanged.
        var noRate = await NewService(db).ConvertToBaseAsync(100m, "EUR", DateTimeOffset.UtcNow, new CurrencyScope(Tenant, Org));
        Assert.False(noRate.Converted);
        Assert.Equal(100m, noRate.Amount);
        Assert.Equal("USD", noRate.BaseCurrencyCode);
    }

    [Fact]
    public async Task GetRate_falls_back_to_a_previous_day_within_maxDaysBack()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("fb-" + Guid.NewGuid().ToString("N")).Options;
        var registry = new ModuleRegistry(new IModule[] { new CurrenciesModule() });
        await using var db = new AppDbContext(options, registry);
        db.Database.EnsureCreated();
        db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
        db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
        // Rate only exists 3 days ago.
        SeedRate(db, "EUR", "USD", 1.2m, TodayUtcMidnight().AddDays(-3));
        await db.SaveChangesAsync();

        var result = await NewService(db).GetRateAsync("EUR", "USD", DateTimeOffset.UtcNow, new CurrencyScope(Tenant, Org), maxDaysBack: 5);
        Assert.Single(result.Rates);
        Assert.Equal(TodayUtcMidnight().AddDays(-3), result.ActualDate);

        // With a tighter window the fallback fails.
        var tight = await NewService(db).GetRateAsync("EUR", "USD", DateTimeOffset.UtcNow, new CurrencyScope(Tenant, Org), maxDaysBack: 1);
        Assert.Empty(tight.Rates);
    }

    // ---- Currencies CRUD (factory + command bus) ---------------------------------------------

    [Fact]
    public async Task List_currencies_returns_envelope_sorted_by_code_and_filters_isBase()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
            db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
            db.Set<Currency>().Add(NewCurrency("PLN", "Polish Zloty"));
        });

        var res = await h.Client.GetAsync("/api/currencies/currencies");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var codes = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()).ToArray();
        Assert.Equal(new[] { "EUR", "PLN", "USD" }, codes); // default sort code ASC

        var baseOnly = await ReadJson(await h.Client.GetAsync("/api/currencies/currencies?isBase=true"));
        Assert.Equal(1, baseOnly.GetProperty("total").GetInt32());
        Assert.Equal("USD", baseOnly.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_currency_returns_201_emits_event_and_rejects_duplicate_code()
    {
        await using var h = await BuildAsync();

        var res = await h.Client.PostAsync("/api/currencies/currencies",
            new StringContent("{\"code\":\"eur\",\"name\":\"Euro\",\"symbol\":\"€\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await ReadJson(res)).GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Contains("currencies.currency.created", h.Events.Published);

        // Code is normalized to upper-case on the persisted row.
        var list = await ReadJson(await h.Client.GetAsync("/api/currencies/currencies"));
        Assert.Equal("EUR", list.GetProperty("items")[0].GetProperty("code").GetString());

        var dup = await h.Client.PostAsync("/api/currencies/currencies",
            new StringContent("{\"code\":\"EUR\",\"name\":\"Euro 2\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Create_currency_with_invalid_code_returns_400()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/currencies/currencies",
            new StringContent("{\"code\":\"EURO\",\"name\":\"Euro\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Invalid input", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_base_currency_demotes_the_previous_base()
    {
        Currency usd = NewCurrency("USD", "US Dollar", isBase: true);
        await using var h = await BuildAsync(seed: db => db.Set<Currency>().Add(usd));

        var res = await h.Client.PostAsync("/api/currencies/currencies",
            new StringContent("{\"code\":\"EUR\",\"name\":\"Euro\",\"isBase\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var list = await ReadJson(await h.Client.GetAsync("/api/currencies/currencies?isBase=true"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal("EUR", list.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_base_currency_returns_400()
    {
        Currency usd = NewCurrency("USD", "US Dollar", isBase: true);
        await using var h = await BuildAsync(seed: db => db.Set<Currency>().Add(usd));

        var res = await h.Client.DeleteAsync($"/api/currencies/currencies?id={usd.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("base currency", (await ReadJson(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_currency_with_active_rate_returns_400_else_soft_deletes()
    {
        Currency usd = NewCurrency("USD", "US Dollar", isBase: true);
        Currency eur = NewCurrency("EUR", "Euro");
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Currency>().Add(usd);
            db.Set<Currency>().Add(eur);
            SeedRate(db, "EUR", "USD", 1.1m, TodayUtcMidnight());
        });

        var blocked = await h.Client.DeleteAsync($"/api/currencies/currencies?id={eur.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("active exchange rate", (await ReadJson(blocked)).GetProperty("error").GetString());
    }

    // ---- Exchange rates CRUD -----------------------------------------------------------------

    [Fact]
    public async Task Create_exchange_rate_requires_existing_currencies_and_rejects_duplicates()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
            db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
        });

        var payload = "{\"fromCurrencyCode\":\"EUR\",\"toCurrencyCode\":\"USD\",\"rate\":\"1.10000000\",\"date\":\"2026-01-01T00:00:00Z\",\"source\":\"nbp\"}";
        var res = await h.Client.PostAsync("/api/currencies/exchange-rates", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Contains("currencies.exchange_rate.created", h.Events.Published);

        // Duplicate (same pair+date+source) → 409.
        var dup = await h.Client.PostAsync("/api/currencies/exchange-rates", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        // Unknown currency → 400.
        var bad = "{\"fromCurrencyCode\":\"GBP\",\"toCurrencyCode\":\"USD\",\"rate\":\"1.2\",\"date\":\"2026-01-01T00:00:00Z\",\"source\":\"nbp\"}";
        var badRes = await h.Client.PostAsync("/api/currencies/exchange-rates", new StringContent(bad, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);
    }

    [Fact]
    public async Task Create_exchange_rate_with_same_from_and_to_returns_400()
    {
        await using var h = await BuildAsync(seed: db => db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true)));
        var payload = "{\"fromCurrencyCode\":\"USD\",\"toCurrencyCode\":\"USD\",\"rate\":\"1\",\"date\":\"2026-01-01T00:00:00Z\",\"source\":\"nbp\"}";
        var res = await h.Client.PostAsync("/api/currencies/exchange-rates", new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Options_endpoint_returns_code_label_pairs()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
            db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/currencies/options"));
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("EUR", items[0].GetProperty("value").GetString());
        Assert.Equal("EUR - Euro", items[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Convert_endpoint_converts_to_base()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Currency>().Add(NewCurrency("USD", "US Dollar", isBase: true));
            db.Set<Currency>().Add(NewCurrency("EUR", "Euro"));
            SeedRate(db, "EUR", "USD", 1.1m, TodayUtcMidnight());
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/currencies/convert?amount=200&from=EUR"));
        Assert.True(body.GetProperty("converted").GetBoolean());
        Assert.Equal("USD", body.GetProperty("baseCurrencyCode").GetString());
        Assert.Equal(220m, body.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Unauthenticated_currencies_list_returns_401()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/currencies/currencies");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
