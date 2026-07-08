using System.Net;
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
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Customers;

/// <summary>
/// End-to-end HTTP tests for the customers Phase-4 dashboard-widget endpoints. Verifies the exact
/// response shapes documented in the port contract ("Dashboard widgets"): new-customers / new-deals /
/// next-interactions item projections + ordering, the customer-todos legacy+canonical merge shape,
/// widget-scope gating (401 unauthenticated, 403 missing feature), and invalid-query → 400.
/// </summary>
public class CustomersPhase4Tests
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
            return Task.FromResult<CommandContext?>(new CommandContext
            {
                TenantId = Tenant, OrganizationId = Org, UserId = User,
                OrganizationIds = new[] { Org }, AllowedOrganizationIds = new[] { Org },
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
        var dbName = "customers-p4-tests-" + Guid.NewGuid().ToString("N");
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

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static CustomerEntity Entity(string kind, string name, DateTimeOffset createdAt, Guid? owner = null, DateTimeOffset? nextAt = null) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, Kind = kind, DisplayName = name,
        OwnerUserId = owner, NextInteractionAt = nextAt, IsActive = true, CreatedAt = createdAt, UpdatedAt = createdAt,
    };

    // ---- new-customers ----------------------------------------------------------------------------

    [Fact]
    public async Task NewCustomers_returns_items_newest_first_with_exact_shape()
    {
        var ownerId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow.AddHours(-3);
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<CustomerEntity>().AddRange(
                Entity("person", "Alice", baseTime, ownerId),
                Entity("company", "Acme", baseTime.AddHours(1)),
                Entity("person", "Bob", baseTime.AddHours(2)));
        });

        var res = await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        var items = body.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        // newest first (Bob, Acme, Alice)
        Assert.Equal("Bob", items[0].GetProperty("displayName").GetString());
        Assert.Equal("Acme", items[1].GetProperty("displayName").GetString());
        Assert.Equal("Alice", items[2].GetProperty("displayName").GetString());

        var alice = items[2];
        Assert.False(string.IsNullOrEmpty(alice.GetProperty("id").GetString()));
        Assert.Equal("person", alice.GetProperty("kind").GetString());
        Assert.Equal(Org.ToString(), alice.GetProperty("organizationId").GetString());
        Assert.Equal(ownerId.ToString(), alice.GetProperty("ownerUserId").GetString());
        Assert.False(string.IsNullOrEmpty(alice.GetProperty("createdAt").GetString()));
        // Company row owner is null.
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("ownerUserId").ValueKind);
    }

    [Fact]
    public async Task NewCustomers_kind_filter_limits_results()
    {
        var now = DateTimeOffset.UtcNow;
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<CustomerEntity>().AddRange(
                Entity("person", "Alice", now.AddMinutes(-2)),
                Entity("company", "Acme", now.AddMinutes(-1)));
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers?kind=company"));
        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("Acme", items[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task NewCustomers_limit_respected_and_invalid_limit_400()
    {
        var now = DateTimeOffset.UtcNow;
        await using var h = await BuildAsync(seed: db =>
        {
            for (var i = 0; i < 4; i++) db.Set<CustomerEntity>().Add(Entity("person", $"P{i}", now.AddMinutes(-i)));
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers?limit=2"));
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());

        var bad = await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers?limit=99");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("Invalid query parameters", (await ReadJson(bad)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task NewCustomers_requires_authentication()
    {
        await using var h = await BuildAsync(new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task NewCustomers_requires_widget_feature()
    {
        await using var h = await BuildAsync(new StubRequestContext(hasFeatures: false));
        var res = await h.Client.GetAsync("/api/customers/dashboard/widgets/new-customers");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- new-deals --------------------------------------------------------------------------------

    [Fact]
    public async Task NewDeals_returns_items_with_string_value_amount()
    {
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<CustomerDeal>().Add(new CustomerDeal
            {
                Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, Title = "Big Deal",
                Status = "open", ValueAmount = 1500.50m, ValueCurrency = "USD", OwnerUserId = ownerId,
                CreatedAt = now, UpdatedAt = now,
            });
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/customers/dashboard/widgets/new-deals"));
        var item = body.GetProperty("items")[0];
        Assert.Equal("Big Deal", item.GetProperty("title").GetString());
        Assert.Equal("open", item.GetProperty("status").GetString());
        Assert.Equal(Org.ToString(), item.GetProperty("organizationId").GetString());
        Assert.Equal(ownerId.ToString(), item.GetProperty("ownerUserId").GetString());
        // valueAmount is a STRING per contract (new-deals valueAmount:string|null).
        Assert.Equal(JsonValueKind.String, item.GetProperty("valueAmount").ValueKind);
        Assert.Equal("1500.50", item.GetProperty("valueAmount").GetString());
        Assert.Equal("USD", item.GetProperty("valueCurrency").GetString());
    }

    // ---- next-interactions ------------------------------------------------------------------------

    [Fact]
    public async Task NextInteractions_returns_future_only_by_default_with_now_field()
    {
        var now = DateTimeOffset.UtcNow;
        await using var h = await BuildAsync(seed: db =>
        {
            var past = Entity("person", "PastGuy", now.AddDays(-5), nextAt: now.AddDays(-1));
            past.NextInteractionName = "Overdue"; past.NextInteractionIcon = "clock"; past.NextInteractionColor = "red";
            var future = Entity("company", "FutureCo", now.AddDays(-5), nextAt: now.AddDays(2));
            future.NextInteractionName = "Kickoff";
            db.Set<CustomerEntity>().AddRange(past, future);
        });

        var body = await ReadJson(await h.Client.GetAsync("/api/customers/dashboard/widgets/next-interactions"));
        Assert.True(body.TryGetProperty("now", out var nowProp) && nowProp.ValueKind == JsonValueKind.String);
        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("FutureCo", items[0].GetProperty("displayName").GetString());
        Assert.Equal("Kickoff", items[0].GetProperty("nextInteractionName").GetString());
        Assert.False(string.IsNullOrEmpty(items[0].GetProperty("nextInteractionAt").GetString()));

        // includePast=true surfaces past reminders too, ordered ascending (oldest first).
        var withPast = await ReadJson(await h.Client.GetAsync("/api/customers/dashboard/widgets/next-interactions?includePast=true"));
        var pastItems = withPast.GetProperty("items");
        Assert.Equal(2, pastItems.GetArrayLength());
        Assert.Equal("PastGuy", pastItems[0].GetProperty("displayName").GetString());
    }

    // ---- customer-todos ---------------------------------------------------------------------------

    [Fact]
    public async Task CustomerTodos_merges_legacy_and_canonical_rows_with_shape()
    {
        var entityId = Guid.NewGuid();
        var interactionId = Guid.NewGuid();
        var legacyTodoId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<CustomerEntity>().Add(new CustomerEntity
            {
                Id = entityId, OrganizationId = Org, TenantId = Tenant, Kind = "person", DisplayName = "Casey",
                CreatedAt = now.AddDays(-10), UpdatedAt = now.AddDays(-10),
            });
            // Canonical: a task interaction bridged from the todo adapter.
            db.Set<CustomerInteraction>().Add(new CustomerInteraction
            {
                Id = interactionId, OrganizationId = Org, TenantId = Tenant, EntityId = entityId,
                InteractionType = "task", Source = "adapter:todo", Title = "Follow up call", Status = "planned",
                CreatedAt = now, UpdatedAt = now,
            });
            // Legacy: a todo bridge link.
            db.Set<CustomerTodoLink>().Add(new CustomerTodoLink
            {
                Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, EntityId = entityId,
                TodoId = legacyTodoId, TodoSource = "example:todo", CreatedAt = now.AddMinutes(-30),
            });
        });

        var res = await h.Client.GetAsync("/api/customers/dashboard/widgets/customer-todos");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var items = (await ReadJson(res)).GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        // Newest first → the canonical interaction row.
        var canonical = items[0];
        Assert.Equal(interactionId.ToString(), canonical.GetProperty("todoId").GetString());
        Assert.Equal("customers:interaction", canonical.GetProperty("todoSource").GetString());
        Assert.Equal("Follow up call", canonical.GetProperty("todoTitle").GetString());
        Assert.Equal(Org.ToString(), canonical.GetProperty("organizationId").GetString());
        var entity = canonical.GetProperty("entity");
        Assert.Equal(entityId.ToString(), entity.GetProperty("id").GetString());
        Assert.Equal("Casey", entity.GetProperty("displayName").GetString());
        Assert.Equal("person", entity.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, entity.GetProperty("ownerUserId").ValueKind);

        // The legacy row keeps its source; detail (title) is deferred → null.
        var legacy = items[1];
        Assert.Equal(legacyTodoId.ToString(), legacy.GetProperty("todoId").GetString());
        Assert.Equal("example:todo", legacy.GetProperty("todoSource").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("todoTitle").ValueKind);
    }
}
