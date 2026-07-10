using System.Linq.Expressions;
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
using Xunit;

namespace OpenMercato.Tests.Crud;

/// <summary>
/// HTTP-level tests for the CRUD factory (OpenMercato.Core.Crud.CrudRoute): the list envelope shape +
/// pagination/sort/soft-delete, single-item 404, command-backed create/update/delete dispatch with
/// events + indexer + custom-field hooks, the optimistic-lock 409, and the 401/403/501 guards.
/// </summary>
public class CrudRouteTests
{
    // ---- Test entity + commands --------------------------------------------------------------

    public sealed class Widget
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid OrganizationId { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
    }

    public sealed record CreateWidgetInput(string Name);
    public sealed record UpdateWidgetInput(Guid Id, string Name);
    public sealed record DeleteWidgetInput(Guid Id);
    public sealed record WidgetResult(string Id);

    private sealed class CreateWidgetCommand :
        ICommand<CreateWidgetInput, WidgetResult>, IUndoableCommand, ICommandLogMetadataBuilder<CreateWidgetInput, WidgetResult>
    {
        public string CommandId => "test.widget.create";

        public async Task<WidgetResult> ExecuteAsync(CreateWidgetInput input, CommandContext ctx, IServiceProvider services)
        {
            var db = services.GetRequiredService<AppDbContext>();
            var now = DateTimeOffset.UtcNow;
            var w = new Widget
            {
                Id = Guid.NewGuid(),
                TenantId = ctx.TenantId!.Value,
                OrganizationId = ctx.OrganizationId!.Value,
                Name = input.Name,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Set<Widget>().Add(w);
            await Task.CompletedTask;
            return new WidgetResult(w.Id.ToString());
        }

        public CommandLogMetadata BuildLog(CreateWidgetInput input, WidgetResult result, CommandContext ctx) =>
            new() { ActionLabel = "Create widget", ResourceKind = "test.widget", ResourceId = result.Id };

        public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
        {
            var db = services.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(log.ResourceId!);
            var w = await db.Set<Widget>().FirstOrDefaultAsync(x => x.Id == id);
            if (w is not null) w.DeletedAt = DateTimeOffset.UtcNow;
        }

        public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
        {
            var db = services.GetRequiredService<AppDbContext>();
            var id = Guid.Parse(log.ResourceId!);
            var w = await db.Set<Widget>().FirstOrDefaultAsync(x => x.Id == id);
            if (w is not null) w.DeletedAt = null;
        }
    }

    private sealed class UpdateWidgetCommand : ICommand<UpdateWidgetInput, WidgetResult>
    {
        public string CommandId => "test.widget.update";

        public async Task<WidgetResult> ExecuteAsync(UpdateWidgetInput input, CommandContext ctx, IServiceProvider services)
        {
            var db = services.GetRequiredService<AppDbContext>();
            var w = await db.Set<Widget>().FirstOrDefaultAsync(x =>
                x.Id == input.Id && x.TenantId == ctx.TenantId && x.DeletedAt == null);
            if (w is null) throw CommandHttpException.NotFound();
            // Optimistic-lock guard reads the expected version from ctx.Headers (spec 02 R40).
            OptimisticLock.Enforce("test.widget", input.Id.ToString(), w.UpdatedAt.UtcDateTime, ctx);
            w.Name = input.Name;
            w.UpdatedAt = DateTimeOffset.UtcNow;
            return new WidgetResult(w.Id.ToString());
        }
    }

    private sealed class DeleteWidgetCommand : ICommand<DeleteWidgetInput, WidgetResult>
    {
        public string CommandId => "test.widget.delete";

        public async Task<WidgetResult> ExecuteAsync(DeleteWidgetInput input, CommandContext ctx, IServiceProvider services)
        {
            var db = services.GetRequiredService<AppDbContext>();
            var w = await db.Set<Widget>().FirstOrDefaultAsync(x =>
                x.Id == input.Id && x.TenantId == ctx.TenantId && x.DeletedAt == null);
            if (w is null) throw CommandHttpException.NotFound();
            w.DeletedAt = DateTimeOffset.UtcNow;
            return new WidgetResult(w.Id.ToString());
        }
    }

    // ---- Test module (maps Widget + registers the commands) -----------------------------------

    private sealed class TestCrudModule : IModule
    {
        public string Id => "test_crud";
        public IReadOnlyList<string> AclFeatures { get; } = new[] { "test.widget.view", "test.widget.manage" };

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped<ICommand, CreateWidgetCommand>();
            services.AddScoped<ICommand, UpdateWidgetCommand>();
            services.AddScoped<ICommand, DeleteWidgetCommand>();
        }

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(e =>
            {
                e.ToTable("test_widgets");
                e.HasKey(x => x.Id);
            });
        }

        public void MapRoutes(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder routes) { }
    }

    // ---- Recording extension-point implementations --------------------------------------------

    private sealed class RecordingEventBus : IEventBus
    {
        public List<(string Event, string Payload)> Published { get; } = new();
        public Task PublishAsync(string eventName, object payload, CancellationToken ct = default)
        {
            Published.Add((eventName, JsonSerializer.Serialize(payload)));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIndexer : ICrudIndexer
    {
        public List<(string Action, string EntityType, string Id)> Calls { get; } = new();
        public Task UpsertOneAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, string crudAction, CancellationToken ct = default)
        { Calls.Add(("upsert:" + crudAction, entityType, recordId)); return Task.CompletedTask; }
        public Task DeleteOneAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
        { Calls.Add(("delete", entityType, recordId)); return Task.CompletedTask; }
    }

    private sealed class RecordingCustomFields : ICrudCustomFields
    {
        public int ListMergeCalls;
        public Task MergeIntoListItemsAsync(string entityType, IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, CancellationToken ct = default)
        {
            ListMergeCalls++;
            foreach (var item in items) item["cfMerged"] = true; // marker so the wire shows the hook ran
            return Task.CompletedTask;
        }
        public Task MergeIntoDetailAsync(string entityType, IDictionary<string, object?> item, CommandContext ctx, CancellationToken ct = default)
        { item["cfMerged"] = true; return Task.CompletedTask; }
        public Task PersistAsync(string entityType, string recordId, JsonElement body, CommandContext ctx, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly Guid _tenant;
        private readonly Guid _org;
        private readonly IReadOnlyList<Guid>? _orgIds;
        private readonly bool _authenticated;
        private readonly bool _hasFeatures;

        public StubRequestContext(Guid tenant, Guid org, IReadOnlyList<Guid>? orgIds, bool authenticated = true, bool hasFeatures = true)
        { _tenant = tenant; _org = org; _orgIds = orgIds; _authenticated = authenticated; _hasFeatures = hasFeatures; }

        public Task<CommandContext?> ResolveAsync(HttpContext http)
        {
            if (!_authenticated) return Task.FromResult<CommandContext?>(null);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in http.Request.Headers) headers[h.Key] = h.Value.ToString();
            return Task.FromResult<CommandContext?>(new CommandContext
            {
                TenantId = _tenant,
                OrganizationId = _org,
                UserId = Guid.NewGuid(),
                OrganizationIds = _orgIds,
                Headers = headers,
            });
        }

        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(_hasFeatures);
    }

    // ---- Harness ------------------------------------------------------------------------------

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static CrudConfig<Widget> WidgetConfig(IReadOnlyList<string>? exportFormats = null) => new()
    {
        BasePath = "test_crud/widgets",
        EntityType = "test_crud:widget",
        ResourceKind = "test.widget",
        ExportFormats = exportFormats,
        ListFeatures = new[] { "test.widget.view" },
        CreateFeatures = new[] { "test.widget.manage" },
        UpdateFeatures = new[] { "test.widget.manage" },
        DeleteFeatures = new[] { "test.widget.manage" },
        IdSelector = w => w.Id,
        DeletedAtSelector = w => w.DeletedAt,
        TenantIdSelector = w => w.TenantId,
        OrganizationIdSelector = w => w.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<Widget>, bool, IOrderedQueryable<Widget>>>
        {
            ["name"] = (q, desc) => desc ? q.OrderByDescending(w => w.Name) : q.OrderBy(w => w.Name),
        },
        ProjectItem = w => new Dictionary<string, object?> { ["id"] = w.Id.ToString(), ["name"] = w.Name },
        CreatedEvent = "test.widget.created",
        UpdatedEvent = "test.widget.updated",
        DeletedEvent = "test.widget.deleted",
        CreateDispatch = async m =>
        {
            var input = JsonSerializer.Deserialize<CreateWidgetInput>(m.Body, Web)!;
            var r = await m.Bus.ExecuteWithLog<CreateWidgetInput, WidgetResult>("test.widget.create", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.Id, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var input = JsonSerializer.Deserialize<UpdateWidgetInput>(m.Body, Web)!;
            var r = await m.Bus.ExecuteWithLog<UpdateWidgetInput, WidgetResult>("test.widget.update", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.Id, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g) ? g : Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<DeleteWidgetInput, WidgetResult>("test.widget.delete", new DeleteWidgetInput(id), m.Ctx);
            return new CrudMutationOutcome(r.Result.Id, r.LogEntry);
        },
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Harness(WebApplication App, HttpClient Client, RecordingEventBus Events, RecordingIndexer Indexer, RecordingCustomFields CustomFields) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(StubRequestContext? requestContext = null, Action<AppDbContext>? seed = null, CrudConfig<Widget>? config = null)
    {
        var events = new RecordingEventBus();
        var indexer = new RecordingIndexer();
        var customFields = new RecordingCustomFields();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var registry = new ModuleRegistry(new IModule[] { new AuditLogsModule(), new TestCrudModule() });
        builder.Services.AddSingleton(registry);
        var dbName = "crud-tests-" + Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        registry.ConfigureServices(builder.Services);
        builder.Services.AddOpenMercatoCrud();
        // Override the no-op defaults with recording implementations + a stub auth bridge.
        builder.Services.AddScoped<ICrudRequestContext>(_ => requestContext ?? new StubRequestContext(Tenant, Org, null));
        builder.Services.AddScoped<ICrudCustomFields>(_ => customFields);
        builder.Services.AddScoped<ICrudIndexer>(_ => indexer);
        builder.Services.AddSingleton<IEventBus>(events);

        var app = builder.Build();
        CrudRoute.Map(app, config ?? WidgetConfig());

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            seed?.Invoke(db);
            db.SaveChanges();
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient(), events, indexer, customFields);
    }

    private static Widget NewWidget(string name, bool deleted = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        OrganizationId = Org,
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
    };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    // ---- Tests --------------------------------------------------------------------------------

    [Fact]
    public async Task List_returns_canonical_envelope_with_pagination()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            for (var i = 0; i < 7; i++) db.Set<Widget>().Add(NewWidget($"w{i}"));
        });

        var res = await h.Client.GetAsync("/api/test_crud/widgets?page=2&pageSize=3");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);

        Assert.Equal(7, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("page").GetInt32());
        Assert.Equal(3, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.GetProperty("totalPages").GetInt32()); // ceil(7/3)
        Assert.Equal(3, body.GetProperty("items").GetArrayLength());
        // Exact key set on the envelope.
        var keys = body.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "items", "page", "pageSize", "total", "totalPages" }, keys);
    }

    [Fact]
    public async Task List_clamps_pageSize_and_sorts_and_invokes_custom_fields_hook()
    {
        await using var h = await BuildAsync(seed: db =>
        {
            db.Set<Widget>().Add(NewWidget("charlie"));
            db.Set<Widget>().Add(NewWidget("alpha"));
            db.Set<Widget>().Add(NewWidget("bravo"));
        });

        var res = await h.Client.GetAsync("/api/test_crud/widgets?pageSize=999&sortField=name&sortDir=asc");
        var body = await ReadJson(res);
        Assert.Equal(100, body.GetProperty("pageSize").GetInt32()); // clamped to max
        var names = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, names);
        // Custom-field decoration ran (marker key present on each item).
        Assert.All(body.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("cfMerged").GetBoolean()));
        Assert.Equal(1, h.CustomFields.ListMergeCalls);
    }

    [Fact]
    public async Task List_excludes_soft_deleted_unless_withDeleted()
    {
        Widget? live = null;
        await using var h = await BuildAsync(seed: db =>
        {
            live = NewWidget("live");
            db.Set<Widget>().Add(live);
            db.Set<Widget>().Add(NewWidget("gone", deleted: true));
        });

        var res = await h.Client.GetAsync("/api/test_crud/widgets");
        var body = await ReadJson(res);
        Assert.Equal(1, body.GetProperty("total").GetInt32());

        var resAll = await h.Client.GetAsync("/api/test_crud/widgets?withDeleted=true");
        var bodyAll = await ReadJson(resAll);
        Assert.Equal(2, bodyAll.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task List_with_empty_org_scope_returns_200_empty_envelope()
    {
        await using var h = await BuildAsync(
            requestContext: new StubRequestContext(Tenant, Org, Array.Empty<Guid>()),
            seed: db => db.Set<Widget>().Add(NewWidget("hidden")));

        var res = await h.Client.GetAsync("/api/test_crud/widgets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal(0, body.GetProperty("total").GetInt32());
        Assert.Equal(0, body.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Get_single_by_id_returns_item_or_404()
    {
        Widget w = NewWidget("solo");
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(w));

        var ok = await h.Client.GetAsync($"/api/test_crud/widgets?id={w.Id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ReadJson(ok);
        // ?id= is a list filtered to one id → {items:[record],...} envelope (OM parity, not a bare record).
        var item0 = body.GetProperty("items")[0];
        Assert.Equal(w.Id.ToString(), item0.GetProperty("id").GetString());
        Assert.True(item0.GetProperty("cfMerged").GetBoolean()); // detail decoration ran

        var miss = await h.Client.GetAsync($"/api/test_crud/widgets/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        var missBody = await ReadJson(miss);
        Assert.Equal("Not found", missBody.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_dispatches_command_emits_event_indexes_and_returns_201_with_operation_header()
    {
        await using var h = await BuildAsync();

        var res = await h.Client.PostAsync("/api/test_crud/widgets",
            new StringContent("{\"name\":\"new\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJson(res);
        var id = body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));

        // Lifecycle event + index upsert fired with the new id.
        Assert.Contains(h.Events.Published, e => e.Event == "test.widget.created" && e.Payload.Contains(id!));
        Assert.Contains(h.Indexer.Calls, c => c is { Action: "upsert:create", EntityType: "test_crud:widget" } && c.Id == id);

        // x-om-operation header present (undoable create) and decodes to the documented shape.
        Assert.True(res.Headers.TryGetValues("x-om-operation", out var vals));
        var header = vals!.First();
        Assert.StartsWith("omop:", header);
        var decoded = JsonDocument.Parse(Uri.UnescapeDataString(header["omop:".Length..])).RootElement;
        Assert.Equal(id, decoded.GetProperty("resourceId").GetString());
        Assert.Equal("test.widget.create", decoded.GetProperty("commandId").GetString());
        Assert.False(string.IsNullOrEmpty(decoded.GetProperty("undoToken").GetString()));
    }

    [Fact]
    public async Task Update_returns_ok_true_and_emits_updated_event()
    {
        Widget w = NewWidget("before");
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(w));

        var res = await h.Client.PutAsync("/api/test_crud/widgets",
            new StringContent($"{{\"id\":\"{w.Id}\",\"name\":\"after\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Contains(h.Events.Published, e => e.Event == "test.widget.updated");
        Assert.Contains(h.Indexer.Calls, c => c.Action == "upsert:update" && c.Id == w.Id.ToString());
    }

    [Fact]
    public async Task Update_with_mismatched_optimistic_lock_header_returns_409()
    {
        Widget w = NewWidget("locked");
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(w));

        var req = new HttpRequestMessage(HttpMethod.Put, "/api/test_crud/widgets")
        {
            Content = new StringContent($"{{\"id\":\"{w.Id}\",\"name\":\"x\"}}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation(OptimisticLock.HeaderName, "2000-01-01T00:00:00.000Z");
        var res = await h.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("record_modified", body.GetProperty("error").GetString());
        Assert.Equal("optimistic_lock_conflict", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_soft_deletes_returns_ok_true_and_emits_delete_index_call()
    {
        Widget w = NewWidget("doomed");
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(w));

        var res = await h.Client.DeleteAsync($"/api/test_crud/widgets?id={w.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Contains(h.Events.Published, e => e.Event == "test.widget.deleted");
        Assert.Contains(h.Indexer.Calls, c => c.Action == "delete" && c.Id == w.Id.ToString());

        // The row is soft-deleted (invisible to the list).
        var list = await ReadJson(await h.Client.GetAsync("/api/test_crud/widgets"));
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Delete_of_missing_record_returns_404()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.DeleteAsync($"/api/test_crud/widgets?id={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unauthenticated_request_returns_401()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(Tenant, Org, null, authenticated: false));
        var res = await h.Client.GetAsync("/api/test_crud/widgets");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_feature_returns_403_with_required_features()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(Tenant, Org, null, hasFeatures: false));
        var res = await h.Client.GetAsync("/api/test_crud/widgets");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Forbidden", body.GetProperty("error").GetString());
        Assert.Contains("test.widget.view", body.GetProperty("requiredFeatures").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Mutation_with_empty_org_scope_returns_403()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(Tenant, Org, Array.Empty<Guid>()));
        var res = await h.Client.PostAsync("/api/test_crud/widgets",
            new StringContent("{\"name\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Forbidden", body.GetProperty("error").GetString());
    }

    // ---- List export (?format=) ---------------------------------------------------------------

    [Fact]
    public async Task Export_csv_serializes_full_filtered_set_across_pages_with_attachment_headers()
    {
        // Seed more rows than one export batch would return in a single page is impractical here (batch=1000),
        // but we assert the FULL set is exported regardless of the ?pageSize= the caller passes.
        await using var h = await BuildAsync(seed: db =>
        {
            for (var i = 0; i < 7; i++) db.Set<Widget>().Add(NewWidget($"w{i:D2}"));
        });

        var res = await h.Client.GetAsync("/api/test_crud/widgets?format=csv&pageSize=2&sortField=name&sortDir=asc");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=\"widgets.csv\"", res.Content.Headers.GetValues("Content-Disposition").Single());

        var body = await res.Content.ReadAsStringAsync();
        var lines = body.Split('\n');
        // Header (union of projected + cf keys, humanized) + 7 data rows despite pageSize=2.
        Assert.Equal("Id,Name,CfMerged", lines[0]);
        Assert.Equal(8, lines.Length);
        Assert.Equal("w00", lines[1].Split(',')[1]);
        Assert.Equal("w06", lines[7].Split(',')[1]);
        // Custom-field decoration ran on the exported rows too.
        Assert.All(lines[1..], l => Assert.EndsWith(",true", l));
    }

    [Fact]
    public async Task Export_markdown_and_xml_and_json_content_types()
    {
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(NewWidget("solo")));

        var md = await h.Client.GetAsync("/api/test_crud/widgets?format=md");
        Assert.Equal("text/markdown; charset=utf-8", md.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=\"widgets.md\"", md.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.StartsWith("| Id | Name | CfMerged |", await md.Content.ReadAsStringAsync());

        var xml = await h.Client.GetAsync("/api/test_crud/widgets?format=xml");
        Assert.Equal("application/xml; charset=utf-8", xml.Content.Headers.ContentType!.ToString());
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", await xml.Content.ReadAsStringAsync());

        var json = await h.Client.GetAsync("/api/test_crud/widgets?format=json");
        Assert.Equal("application/json; charset=utf-8", json.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=\"widgets.json\"", json.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task Export_with_unknown_format_falls_through_to_json_list_envelope()
    {
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(NewWidget("solo")));

        var res = await h.Client.GetAsync("/api/test_crud/widgets?format=yaml");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType!.ToString());
        Assert.False(res.Content.Headers.Contains("Content-Disposition"));
        var body = await ReadJson(res);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Export_with_disabled_format_falls_through_to_json_list_envelope()
    {
        // ExportFormats restricts to csv only → a json export request returns the normal list envelope (OM parity).
        var config = WidgetConfig(exportFormats: new[] { "csv" });
        await using var h = await BuildAsync(seed: db => db.Set<Widget>().Add(NewWidget("solo")), config: config);

        var json = await h.Client.GetAsync("/api/test_crud/widgets?format=json");
        Assert.Equal("application/json; charset=utf-8", json.Content.Headers.ContentType!.ToString());
        Assert.False(json.Content.Headers.Contains("Content-Disposition"));
        Assert.Equal(1, (await ReadJson(json)).GetProperty("total").GetInt32());

        var csv = await h.Client.GetAsync("/api/test_crud/widgets?format=csv");
        Assert.Equal("text/csv; charset=utf-8", csv.Content.Headers.ContentType!.ToString());
    }
}
