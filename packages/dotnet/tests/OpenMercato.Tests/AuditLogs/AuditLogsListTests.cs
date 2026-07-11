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
