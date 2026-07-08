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
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.Entities.Lib;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Customers;

/// <summary>
/// End-to-end HTTP tests for the customers Phase-1 records surface, wired with the REAL entities
/// custom-field codec + query_index projection + the customers base-row resolver + command handlers:
/// people create→list(cf filter via index)→detail→update→delete round trip; company/address/tag CRUD;
/// cf_ write / bare read; and the documented quirks (delete-id-required 400, tag assign 201 / unassign
/// 200 asymmetry).
/// </summary>
public class CustomersPhase1Tests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class StubRequestContext : ICrudRequestContext
    {
        private readonly bool _authenticated;
        private readonly bool _hasFeatures;
        public StubRequestContext(bool authenticated = true, bool hasFeatures = true)
        { _authenticated = authenticated; _hasFeatures = hasFeatures; }

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

        public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(_hasFeatures);
    }

    private sealed record Harness(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); }
    }

    private static async Task<Harness> BuildAsync(StubRequestContext? requestContext = null, bool installCe = true)
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
        var dbName = "customers-tests-" + Guid.NewGuid().ToString("N");
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
            if (installCe)
                await InstallFromCe.InstallAsync(db, registry, Tenant, organizationId: null);
        }

        await app.StartAsync();
        return new Harness(app, app.GetTestClient());
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    // ---- people round trip --------------------------------------------------------------------

    [Fact]
    public async Task Person_create_list_cf_filter_detail_update_delete_round_trip()
    {
        await using var h = await BuildAsync();

        // Create with a custom field written via the cf_ request-key convention.
        var create = await h.Client.PostAsync("/api/customers/people",
            Body("{\"firstName\":\"Mia\",\"lastName\":\"Johnson\",\"primaryEmail\":\"mia@example.com\",\"cf_buying_role\":\"champion\"}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJson(create);
        var id = created.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(created.GetProperty("personId").GetString()));

        // A second person with a different cf value (index segregation + filter).
        await h.Client.PostAsync("/api/customers/people",
            Body("{\"firstName\":\"Daniel\",\"lastName\":\"Cho\",\"cf_buying_role\":\"influencer\"}"));

        // List all persons (index-backed).
        var list = await ReadJson(await h.Client.GetAsync("/api/customers/people"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());

        // Filter by the custom field through the query index.
        var filtered = await ReadJson(await h.Client.GetAsync("/api/customers/people?cf_buying_role=champion"));
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        var item = filtered.GetProperty("items")[0];
        Assert.Equal("Mia Johnson", item.GetProperty("displayName").GetString());
        Assert.Equal("champion", item.GetProperty("buying_role").GetString()); // bare-key cf read on list

        // Detail: profile + merged custom fields.
        var detail = await ReadJson(await h.Client.GetAsync($"/api/customers/people/{id}"));
        Assert.Equal("Mia", detail.GetProperty("profile").GetProperty("firstName").GetString());
        Assert.Equal("champion", detail.GetProperty("person").GetProperty("buying_role").GetString());

        // Update (returns { ok, updatedAt }).
        var update = await h.Client.PutAsync("/api/customers/people",
            Body($"{{\"id\":\"{id}\",\"jobTitle\":\"VP\"}}"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await ReadJson(update);
        Assert.True(updated.GetProperty("ok").GetBoolean());
        Assert.False(updated.GetProperty("updatedAt").GetString() is null);

        var afterUpdate = await ReadJson(await h.Client.GetAsync($"/api/customers/people/{id}"));
        Assert.Equal("VP", afterUpdate.GetProperty("profile").GetProperty("jobTitle").GetString());

        // Delete (soft) → gone from the list.
        var del = await h.Client.DeleteAsync($"/api/customers/people?id={id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.True((await ReadJson(del)).GetProperty("ok").GetBoolean());

        var afterDelete = await ReadJson(await h.Client.GetAsync("/api/customers/people"));
        Assert.Equal(1, afterDelete.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Person_create_requires_first_and_last_name()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.PostAsync("/api/customers/people", Body("{\"firstName\":\"Solo\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Person_delete_without_id_returns_400_with_exact_message()
    {
        await using var h = await BuildAsync();
        var res = await h.Client.DeleteAsync("/api/customers/people");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Person id is required", (await ReadJson(res)).GetProperty("error").GetString());
    }

    // ---- companies ----------------------------------------------------------------------------

    [Fact]
    public async Task Company_create_and_detail()
    {
        await using var h = await BuildAsync();
        var create = await h.Client.PostAsync("/api/customers/companies",
            Body("{\"displayName\":\"Brightside Solar\",\"industry\":\"Renewable Energy\",\"cf_relationship_health\":\"healthy\"}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await ReadJson(create);
        var id = created.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(created.GetProperty("companyId").GetString()));

        var detail = await ReadJson(await h.Client.GetAsync($"/api/customers/companies/{id}"));
        Assert.Equal("Brightside Solar", detail.GetProperty("company").GetProperty("displayName").GetString());
        Assert.Equal("Renewable Energy", detail.GetProperty("profile").GetProperty("industry").GetString());
        Assert.Equal("healthy", detail.GetProperty("company").GetProperty("relationship_health").GetString());

        var list = await ReadJson(await h.Client.GetAsync("/api/customers/companies?cf_relationship_health=healthy"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    // ---- addresses + tags ---------------------------------------------------------------------

    [Fact]
    public async Task Address_create_and_list_by_entity()
    {
        await using var h = await BuildAsync();
        var person = await ReadJson(await h.Client.PostAsync("/api/customers/people", Body("{\"firstName\":\"Lena\",\"lastName\":\"Ortiz\"}")));
        var entityId = person.GetProperty("id").GetString();

        var create = await h.Client.PostAsync("/api/customers/addresses",
            Body($"{{\"entityId\":\"{entityId}\",\"addressLine1\":\"1 Market St\",\"city\":\"SF\"}}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await ReadJson(await h.Client.GetAsync($"/api/customers/addresses?entityId={entityId}"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal("1 Market St", list.GetProperty("items")[0].GetProperty("addressLine1").GetString());
    }

    [Fact]
    public async Task Tag_create_assign_returns_201_unassign_returns_200()
    {
        await using var h = await BuildAsync();
        var tag = await ReadJson(await h.Client.PostAsync("/api/customers/tags", Body("{\"slug\":\"vip\",\"label\":\"VIP\"}")));
        var tagId = tag.GetProperty("id").GetString();
        var person = await ReadJson(await h.Client.PostAsync("/api/customers/people", Body("{\"firstName\":\"Arjun\",\"lastName\":\"Patel\"}")));
        var entityId = person.GetProperty("id").GetString();

        var assign = await h.Client.PostAsync("/api/customers/tags/assign", Body($"{{\"tagId\":\"{tagId}\",\"entityId\":\"{entityId}\"}}"));
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);

        var unassign = await h.Client.PostAsync("/api/customers/tags/unassign", Body($"{{\"tagId\":\"{tagId}\",\"entityId\":\"{entityId}\"}}"));
        Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);

        // Second unassign → 200 with null id (nothing assigned).
        var again = await h.Client.PostAsync("/api/customers/tags/unassign", Body($"{{\"tagId\":\"{tagId}\",\"entityId\":\"{entityId}\"}}"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await ReadJson(again)).GetProperty("id").ValueKind == JsonValueKind.Null);
    }

    // ---- person↔company links -----------------------------------------------------------------

    [Fact]
    public async Task Person_company_link_create_and_list()
    {
        await using var h = await BuildAsync();
        var company = await ReadJson(await h.Client.PostAsync("/api/customers/companies", Body("{\"displayName\":\"Copperleaf\"}")));
        var companyId = company.GetProperty("id").GetString();
        var person = await ReadJson(await h.Client.PostAsync("/api/customers/people", Body("{\"firstName\":\"Naomi\",\"lastName\":\"Harris\"}")));
        var personId = person.GetProperty("id").GetString();

        var link = await h.Client.PostAsync($"/api/customers/people/{personId}/companies",
            Body($"{{\"companyId\":\"{companyId}\",\"isPrimary\":true}}"));
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        Assert.True((await ReadJson(link)).GetProperty("ok").GetBoolean());

        var companies = await ReadJson(await h.Client.GetAsync($"/api/customers/people/{personId}/companies"));
        Assert.Equal(1, companies.GetProperty("items").GetArrayLength());
        Assert.Equal("Copperleaf", companies.GetProperty("items")[0].GetProperty("displayName").GetString());

        var people = await ReadJson(await h.Client.GetAsync($"/api/customers/companies/{companyId}/people"));
        Assert.Equal(1, people.GetProperty("total").GetInt32());
    }

    // ---- auth ---------------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_returns_401()
    {
        await using var h = await BuildAsync(requestContext: new StubRequestContext(authenticated: false));
        var res = await h.Client.GetAsync("/api/customers/people");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
