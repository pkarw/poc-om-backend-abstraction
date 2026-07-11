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
using OpenMercato.Core.Modules;
using Xunit;

namespace OpenMercato.Tests.AuditLogs;

public class AuditLogsListTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _tenantViewer;
        public StubRequestContext(bool tenantViewer) => _tenantViewer = tenantViewer;
        public Task<CommandContext?> ResolveAsync(HttpContext http) =>
            Task.FromResult<CommandContext?>(new CommandContext { TenantId = Tenant, OrganizationId = Org, UserId = Me, OrganizationIds = new[] { Org } });
        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) =>
            Task.FromResult(features.Contains("audit_logs.view_tenant") ? _tenantViewer : true);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(bool tenantViewer, Action<AppDbContext> seed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var registry = new ModuleRegistry(new IModule[] { new AuditLogsModule() });
        builder.Services.AddSingleton(registry);
        var dbName = "audit-list-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddScoped<ICrudRequestContext>(_ => new StubRequestContext(tenantViewer));

        var app = builder.Build();
        new AuditLogsModule().MapRoutes(app);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            seed(db);
            db.SaveChanges();
        }
        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static void Log(AppDbContext db, Guid actor, string kind, string resId, DateTime createdAt, string? changes = null) =>
        db.Set<ActionLog>().Add(new ActionLog
        {
            Id = Guid.NewGuid(), TenantId = Tenant, OrganizationId = Org, ActorUserId = actor,
            CommandId = "customers.people.update", ActionType = "update", ActionLabel = "Updated person",
            ResourceKind = kind, ResourceId = resId, ExecutionState = "done", ChangesJson = changes,
            CreatedAt = createdAt, UpdatedAt = createdAt,
        });

    private static async Task<JsonElement> Get(HttpClient c, string url) =>
        JsonDocument.Parse(await (await c.GetAsync(url)).Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Filters_by_resource_and_returns_total_when_requested()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(true, db =>
        {
            Log(db, Me, "customers.company", "c1", t0);
            Log(db, Me, "customers.company", "c1", t0.AddMinutes(1));
            Log(db, Me, "customers.company", "c2", t0);
        });
        var json = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1&includeTotal=true");
        Assert.Equal(2, json.GetProperty("total").GetInt32());
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("c1", i.GetProperty("resourceId").GetString()));
        // newest first
        Assert.True(string.CompareOrdinal(items[0].GetProperty("createdAt").GetString(), items[1].GetProperty("createdAt").GetString()) > 0);
    }

    [Fact]
    public async Task Self_viewer_only_sees_own_actions()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(tenantViewer: false, db =>
        {
            Log(db, Me, "customers.company", "c1", t0);
            Log(db, Other, "customers.company", "c1", t0);
        });
        var json = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1&includeTotal=true");
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(Me.ToString(), json.GetProperty("items").EnumerateArray().Single().GetProperty("actorUserId").GetString());
    }

    [Fact]
    public async Task Envelope_carries_canViewTenant_page_and_paginates()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(true, db =>
        {
            for (var i = 0; i < 7; i++) Log(db, Me, "customers.company", "c1", t0.AddMinutes(i));
        });
        var p1 = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1&pageSize=5&page=1");
        Assert.True(p1.GetProperty("canViewTenant").GetBoolean());
        Assert.Equal(1, p1.GetProperty("page").GetInt32());
        Assert.Equal(5, p1.GetProperty("pageSize").GetInt32());
        Assert.Equal(7, p1.GetProperty("total").GetInt32());
        Assert.Equal(2, p1.GetProperty("totalPages").GetInt32());
        Assert.Equal(5, p1.GetProperty("items").GetArrayLength());

        var p2 = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1&pageSize=5&page=2");
        Assert.Equal(2, p2.GetProperty("page").GetInt32());
        Assert.Equal(2, p2.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task IncludeRelated_widens_to_parent_resource()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(true, db =>
        {
            Log(db, Me, "customers.company", "c1", t0);
            db.Set<ActionLog>().Add(new ActionLog
            {
                Id = Guid.NewGuid(), TenantId = Tenant, OrganizationId = Org, ActorUserId = Me,
                CommandId = "customers.interactions.create", ActionType = "create", ResourceKind = "customers.interaction",
                ResourceId = "i1", ParentResourceKind = "customers.company", ParentResourceId = "c1",
                ExecutionState = "done", CreatedAt = t0.AddMinutes(1), UpdatedAt = t0.AddMinutes(1),
            });
        });
        var without = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1");
        Assert.Equal(1, without.GetProperty("items").GetArrayLength());
        var with = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.company&resourceId=c1&includeRelated=true");
        Assert.Equal(2, with.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Export_returns_csv_with_fixed_header_and_change_rows()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(true, db =>
            Log(db, Me, "customers.person", "p1", t0, changes: "{\"displayName\":{\"from\":\"A\",\"to\":\"B\"}}"));
        var res = await h.Client.GetAsync("/api/audit_logs/audit-logs/actions/export?resourceKind=customers.person&resourceId=p1");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        var csv = await res.Content.ReadAsStringAsync();
        var header = csv.Split('\n')[0].TrimEnd('\r');
        Assert.Equal("When,User,Action,Field,Old Value,New Value,Source", header);
        Assert.Contains("displayName", csv);
        Assert.Contains(",Update,", csv); // derived from customers.person.update
    }

    [Fact]
    public async Task FieldName_filter_matches_changes_object()
    {
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var h = await BuildAsync(true, db =>
        {
            Log(db, Me, "customers.person", "p1", t0, changes: "{\"displayName\":{\"from\":\"A\",\"to\":\"B\"}}");
            Log(db, Me, "customers.person", "p1", t0.AddMinutes(1), changes: "{\"primaryEmail\":{\"from\":null,\"to\":\"x@y.z\"}}");
        });
        var json = await Get(h.Client, "/api/audit_logs/audit-logs/actions?resourceKind=customers.person&resourceId=p1&fieldName=displayName&includeTotal=true");
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.True(item.GetProperty("changes").TryGetProperty("displayName", out _));
    }
}
