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
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.Entities.Lib;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Customers;

/// <summary>
/// End-to-end HTTP tests for the Phase-3a deals + pipelines surface: pipeline/stage create; deal
/// create→list(cf filter via query index)→stage change (transition recorded)→kanban aggregate; the
/// documented delete-id-required 400 and the pipeline/stage 201 statuses.
/// </summary>
public class CustomersPhase3aTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _authenticated;
        public StubRequestContext(bool authenticated = true) => _authenticated = authenticated;

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
            new AuditLogsModule(),
            new OpenMercato.Modules.Directory.DirectoryModule(),
            new EntitiesModule(),
            new QueryIndexModule(),
            new CustomersModule(),
        });
        builder.Services.AddSingleton(registry);
        var dbName = "customers-p3a-" + Guid.NewGuid().ToString("N");
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
            await InstallFromCe.InstallAsync(db, registry, Tenant, organizationId: null);
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Deal_create_list_cf_filter_stage_change_aggregate_round_trip()
    {
        await using var h = await BuildAsync();

        // Pipeline + two stages.
        var pipeline = await ReadJson(await h.Client.PostAsync("/api/customers/pipelines", Body("{\"name\":\"Sales\",\"isDefault\":true}")));
        var pipelineId = pipeline.GetProperty("id").GetString()!;

        var discovery = await h.Client.PostAsync("/api/customers/pipeline-stages", Body($"{{\"pipelineId\":\"{pipelineId}\",\"label\":\"Discovery\",\"order\":0}}"));
        Assert.Equal(HttpStatusCode.Created, discovery.StatusCode);
        var discoveryId = (await ReadJson(discovery)).GetProperty("id").GetString()!;
        var proposal = await h.Client.PostAsync("/api/customers/pipeline-stages", Body($"{{\"pipelineId\":\"{pipelineId}\",\"label\":\"Proposal\",\"order\":1}}"));
        var proposalId = (await ReadJson(proposal)).GetProperty("id").GetString()!;

        // Two deals with a segregating custom field, both starting in Discovery.
        var create = await h.Client.PostAsync("/api/customers/deals",
            Body($"{{\"title\":\"Big Deal\",\"valueAmount\":1000,\"valueCurrency\":\"USD\",\"pipelineId\":\"{pipelineId}\",\"pipelineStageId\":\"{discoveryId}\",\"cf_competitive_risk\":\"high\"}}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var dealId = (await ReadJson(create)).GetProperty("id").GetString()!;

        await h.Client.PostAsync("/api/customers/deals",
            Body($"{{\"title\":\"Small Deal\",\"valueAmount\":200,\"valueCurrency\":\"USD\",\"pipelineId\":\"{pipelineId}\",\"pipelineStageId\":\"{discoveryId}\",\"cf_competitive_risk\":\"low\"}}"));

        // List (index-backed) → both deals.
        var list = await ReadJson(await h.Client.GetAsync("/api/customers/deals"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());

        // Custom-field filter via the query index → one deal, bare-key cf read on the item.
        var filtered = await ReadJson(await h.Client.GetAsync("/api/customers/deals?cf_competitive_risk=high"));
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        var item = filtered.GetProperty("items")[0];
        Assert.Equal("Big Deal", item.GetProperty("title").GetString());
        Assert.Equal("high", item.GetProperty("competitive_risk").GetString());

        // Stage change → PUT moves the deal to Proposal, recording a stage transition.
        var move = await h.Client.PutAsync("/api/customers/deals", Body($"{{\"id\":\"{dealId}\",\"pipelineStageId\":\"{proposalId}\"}}"));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        Assert.True((await ReadJson(move)).GetProperty("ok").GetBoolean());

        var detail = await ReadJson(await h.Client.GetAsync($"/api/customers/deals/{dealId}?include=stages"));
        Assert.Equal(proposalId, detail.GetProperty("deal").GetProperty("pipelineStageId").GetString());
        Assert.Equal("Proposal", detail.GetProperty("deal").GetProperty("pipelineStage").GetString());
        var transitions = detail.GetProperty("stageTransitions");
        Assert.Contains(transitions.EnumerateArray(), t => t.GetProperty("stageId").GetString() == proposalId);

        // Aggregate → per-stage lanes; Big Deal now in Proposal, Small Deal still in Discovery.
        var aggregate = await ReadJson(await h.Client.GetAsync("/api/customers/deals/aggregate"));
        var perStage = aggregate.GetProperty("perStage");
        var byStage = perStage.EnumerateArray().ToDictionary(s => s.GetProperty("stageId").GetString()!, s => s.GetProperty("count").GetInt32());
        Assert.Equal(1, byStage[discoveryId]);
        Assert.Equal(1, byStage[proposalId]);
    }

    [Fact]
    public async Task Deal_delete_without_id_returns_400_with_exact_message()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.DeleteAsync("/api/customers/deals");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Deal id is required", (await ReadJson(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Deal_stats_requires_closed_deal()
    {
        await using var h = await BuildAsync();
        var create = await h.Client.PostAsync("/api/customers/deals", Body("{\"title\":\"Open Deal\"}"));
        var dealId = (await ReadJson(create)).GetProperty("id").GetString()!;
        var stats = await h.Client.GetAsync($"/api/customers/deals/{dealId}/stats");
        Assert.Equal(HttpStatusCode.BadRequest, stats.StatusCode);
        Assert.Equal("DEAL_NOT_CLOSED", (await ReadJson(stats)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pipeline_delete_with_active_deals_returns_409()
    {
        await using var h = await BuildAsync();
        var pipeline = await ReadJson(await h.Client.PostAsync("/api/customers/pipelines", Body("{\"name\":\"P1\"}")));
        var pipelineId = pipeline.GetProperty("id").GetString()!;
        await h.Client.PostAsync("/api/customers/deals", Body($"{{\"title\":\"D\",\"pipelineId\":\"{pipelineId}\"}}"));

        var del = await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/customers/pipelines")
        {
            Content = Body($"{{\"id\":\"{pipelineId}\"}}"),
        });
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
        Assert.Equal("Cannot delete pipeline with active deals", (await ReadJson(del)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Deals_list_requires_auth()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(), new OpenMercato.Modules.Directory.DirectoryModule(),
            new EntitiesModule(), new QueryIndexModule(), new CustomersModule(),
        });
        builder.Services.AddSingleton(registry);
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("customers-p3a-auth-" + Guid.NewGuid().ToString("N")));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        builder.Services.AddSingleton<IEventBus, LocalEventBus>();
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext(authenticated: false));
        var app = builder.Build();
        new CustomersModule().MapRoutes(app);
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var res = await client.GetAsync("/api/customers/deals");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await app.DisposeAsync();
    }
}
