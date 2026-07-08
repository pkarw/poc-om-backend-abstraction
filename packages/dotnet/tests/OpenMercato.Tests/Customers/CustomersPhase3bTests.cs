using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// End-to-end HTTP tests for the customers Phase-3b timeline surface: interaction cursor-list
/// (<c>{items, nextCursor}</c>), create/complete/cancel lifecycle, counts, email-visibility PATCH, and
/// comment create/list. Exercises the reflection-registered command handlers through the real CommandBus.
/// </summary>
public class CustomersPhase3bTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid PersonId = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _hasFeatures;
        public StubRequestContext(bool hasFeatures = true) { _hasFeatures = hasFeatures; }

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

        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(_hasFeatures);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(Action<AppDbContext>? seed = null)
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
        var dbName = "customers-p3b-tests-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext());

        var app = builder.Build();
        new CustomersModule().MapRoutes(app);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            db.Set<CustomerEntity>().Add(new CustomerEntity
            {
                Id = PersonId, OrganizationId = Org, TenantId = Tenant, Kind = "person",
                DisplayName = "Ada Lovelace", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            seed?.Invoke(db);
            db.SaveChanges();
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> CreateInteraction(HttpClient client, string type, string title, DateTimeOffset scheduledAt, string status = "planned")
    {
        var json = JsonSerializer.Serialize(new
        {
            entityId = PersonId.ToString(), interactionType = type, title, status,
            scheduledAt = scheduledAt.ToUniversalTime().ToString("o"),
        });
        var res = await client.PostAsync("/api/customers/interactions", Body(json));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("id").GetString()!;
    }

    // ---- interaction create + cursor list -----------------------------------------------------

    [Fact]
    public async Task Interaction_create_returns_201_and_cursor_list_envelope()
    {
        await using var h = await BuildAsync();
        var id = await CreateInteraction(h.Client, "call", "Kickoff", DateTimeOffset.UtcNow.AddHours(1));

        var list = await ReadJson(await h.Client.GetAsync($"/api/customers/interactions?entityId={PersonId}"));
        // Cursor envelope: { items, nextCursor } — NOT { total, page, pageSize, totalPages }.
        Assert.True(list.TryGetProperty("items", out var items));
        Assert.True(list.TryGetProperty("nextCursor", out _));
        Assert.False(list.TryGetProperty("total", out _));
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(id, items[0].GetProperty("id").GetString());
        Assert.Equal("call", items[0].GetProperty("interactionType").GetString());
        Assert.Equal("planned", items[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Interaction_cursor_pagination_walks_pages()
    {
        await using var h = await BuildAsync();
        var t0 = DateTimeOffset.UtcNow.AddHours(1);
        var id1 = await CreateInteraction(h.Client, "call", "One", t0);
        var id2 = await CreateInteraction(h.Client, "call", "Two", t0.AddHours(1));
        var id3 = await CreateInteraction(h.Client, "call", "Three", t0.AddHours(2));

        var page1 = await ReadJson(await h.Client.GetAsync($"/api/customers/interactions?entityId={PersonId}&limit=2"));
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(id1, page1.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(id2, page1.GetProperty("items")[1].GetProperty("id").GetString());
        var cursor = page1.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var page2 = await ReadJson(await h.Client.GetAsync($"/api/customers/interactions?entityId={PersonId}&limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(id3, page2.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Interaction_invalid_cursor_returns_400()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.GetAsync($"/api/customers/interactions?cursor=not-base64!");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Invalid cursor", (await ReadJson(res)).GetProperty("error").GetString());
    }

    // ---- complete / cancel --------------------------------------------------------------------

    [Fact]
    public async Task Interaction_complete_sets_done_and_occurredAt()
    {
        await using var h = await BuildAsync();
        var id = await CreateInteraction(h.Client, "meeting", "Demo", DateTimeOffset.UtcNow.AddHours(1));

        var res = await h.Client.PostAsync("/api/customers/interactions/complete", Body($"{{\"id\":\"{id}\"}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Set<CustomerInteraction>().FirstAsync(i => i.Id == Guid.Parse(id));
        Assert.Equal("done", row.Status);
        Assert.NotNull(row.OccurredAt);
    }

    [Fact]
    public async Task Interaction_cancel_sets_canceled()
    {
        await using var h = await BuildAsync();
        var id = await CreateInteraction(h.Client, "meeting", "Demo", DateTimeOffset.UtcNow.AddHours(1));

        var res = await h.Client.PostAsync("/api/customers/interactions/cancel", Body($"{{\"id\":\"{id}\"}}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Set<CustomerInteraction>().FirstAsync(i => i.Id == Guid.Parse(id));
        Assert.Equal("canceled", row.Status);
    }

    [Fact]
    public async Task Interaction_complete_missing_returns_404()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/customers/interactions/complete", Body($"{{\"id\":\"{Guid.NewGuid()}\"}}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("Interaction not found", (await ReadJson(res)).GetProperty("error").GetString());
    }

    // ---- next-interaction projection ----------------------------------------------------------

    [Fact]
    public async Task Interaction_create_recomputes_next_interaction_on_entity()
    {
        await using var h = await BuildAsync();
        var scheduled = DateTimeOffset.UtcNow.AddHours(3);
        await CreateInteraction(h.Client, "call", "Follow up", scheduled);

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().FirstAsync(e => e.Id == PersonId);
        Assert.NotNull(entity.NextInteractionAt);
        Assert.Equal("Follow up", entity.NextInteractionName);
    }

    // ---- counts -------------------------------------------------------------------------------

    [Fact]
    public async Task Interaction_counts_group_by_type()
    {
        await using var h = await BuildAsync();
        var t = DateTimeOffset.UtcNow.AddHours(1);
        await CreateInteraction(h.Client, "call", "c1", t);
        await CreateInteraction(h.Client, "call", "c2", t.AddMinutes(5));
        await CreateInteraction(h.Client, "email", "e1", t.AddMinutes(10));

        var res = await ReadJson(await h.Client.GetAsync($"/api/customers/interactions/counts?entityId={PersonId}"));
        Assert.True(res.GetProperty("ok").GetBoolean());
        var r = res.GetProperty("result");
        Assert.Equal(2, r.GetProperty("call").GetInt32());
        Assert.Equal(1, r.GetProperty("email").GetInt32());
        Assert.Equal(0, r.GetProperty("meeting").GetInt32());
        Assert.Equal(3, r.GetProperty("total").GetInt32());
    }

    // ---- email visibility PATCH ---------------------------------------------------------------

    [Fact]
    public async Task Visibility_patch_non_email_returns_404()
    {
        await using var h = await BuildAsync();
        var id = await CreateInteraction(h.Client, "call", "Ring", DateTimeOffset.UtcNow.AddHours(1));
        var res = await h.Client.PatchAsync($"/api/customers/interactions/{id}/visibility", Body("{\"visibility\":\"shared\"}"));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("Email not found", (await ReadJson(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Visibility_patch_author_flips_and_reports_changed()
    {
        await using var h = await BuildAsync(db => db.Set<CustomerInteraction>().Add(new CustomerInteraction
        {
            Id = Guid.NewGuid(), OrganizationId = Org, TenantId = Tenant, EntityId = PersonId,
            InteractionType = "email", Title = "Hi", Status = "done", Visibility = "private",
            AuthorUserId = User, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }));

        using (var scope = h.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var email = await db.Set<CustomerInteraction>().FirstAsync(i => i.InteractionType == "email");
            var res = await h.Client.PatchAsync($"/api/customers/interactions/{email.Id}/visibility", Body("{\"visibility\":\"shared\"}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await ReadJson(res);
            Assert.True(body.GetProperty("ok").GetBoolean());
            Assert.True(body.GetProperty("changed").GetBoolean());
        }
    }

    [Fact]
    public async Task Visibility_patch_invalid_body_returns_422()
    {
        await using var h = await BuildAsync();
        var id = await CreateInteraction(h.Client, "call", "Ring", DateTimeOffset.UtcNow.AddHours(1));
        var res = await h.Client.PatchAsync($"/api/customers/interactions/{id}/visibility", Body("{\"visibility\":\"bogus\"}"));
        Assert.Equal((HttpStatusCode)422, res.StatusCode);
    }

    // ---- comments -----------------------------------------------------------------------------

    [Fact]
    public async Task Comment_create_returns_201_with_author_and_lists()
    {
        await using var h = await BuildAsync();
        var create = await h.Client.PostAsync("/api/customers/comments", Body($"{{\"entityId\":\"{PersonId}\",\"body\":\"Great call\"}}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await ReadJson(create);
        var id = body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(User.ToString(), body.GetProperty("authorUserId").GetString());

        var list = await ReadJson(await h.Client.GetAsync($"/api/customers/comments?entityId={PersonId}"));
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        Assert.Equal("Great call", list.GetProperty("items")[0].GetProperty("body").GetString());
        Assert.Equal(id, list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Comment_create_missing_body_returns_400()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/customers/comments", Body($"{{\"entityId\":\"{PersonId}\"}}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- activities bridge --------------------------------------------------------------------

    [Fact]
    public async Task Activities_bridge_create_sets_deprecation_headers_and_writes_interaction()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/customers/activities",
            Body($"{{\"entityId\":\"{PersonId}\",\"activityType\":\"note\",\"subject\":\"Called\"}}"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.True(res.Headers.Contains("Deprecation"));

        using var scope = h.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Set<CustomerInteraction>().AnyAsync(i => i.InteractionType == "note" && i.Source == "adapter:activity"));
    }
}
