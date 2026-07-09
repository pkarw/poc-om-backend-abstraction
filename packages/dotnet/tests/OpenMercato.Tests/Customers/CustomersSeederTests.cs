using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Seeding;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.QueryIndex;
using Xunit;

namespace OpenMercato.Tests.Customers;

/// <summary>
/// Locks in the customers seed dataset parity with upstream <c>mercato init</c> (setup.ts
/// seedDefaults + seedExamples): the Default Pipeline (+8 stages) plus 3 companies / 6 people /
/// 6 deals with company+person links. Exercises the SAME split the per-module setup hooks call
/// (<see cref="CustomersSeeder.SeedDefaultsAsync"/> then <see cref="CustomersSeeder.SeedExamplesAsync"/>),
/// and asserts idempotency (a second run adds nothing).
/// </summary>
public class CustomersSeederTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Org = Guid.NewGuid();

    private static ServiceProvider BuildServices()
    {
        var registry = new ModuleRegistry(new IModule[]
        {
            new AuditLogsModule(),
            new OpenMercato.Modules.Directory.DirectoryModule(),
            new EntitiesModule(),
            new QueryIndexModule(),
            new CustomersModule(),
        });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton<INotificationCatalog, NotificationCatalog>();
        services.AddSingleton<ICustomFieldRegistry, CustomFieldRegistry>();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("customers-seeder-" + Guid.NewGuid().ToString("N")));
        registry.ConfigureServices(services);
        services.AddOpenMercatoCrud();
        services.AddSingleton<IEventBus, LocalEventBus>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Seeds_default_pipeline_companies_people_and_six_deals_idempotently()
    {
        using var provider = BuildServices();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var indexer = scope.ServiceProvider.GetRequiredService<ICrudIndexer>();

        await CustomersSeeder.SeedDefaultsAsync(db, registry, Tenant, Org);
        var seeded = await CustomersSeeder.SeedExamplesAsync(db, registry, indexer, Tenant, Org);

        // Default pipeline + its 8 stages (setup.ts seedDefaults → seedDefaultPipeline).
        var pipelines = await db.Set<CustomerPipeline>().CountAsync(p => p.TenantId == Tenant && p.OrganizationId == Org);
        Assert.Equal(1, pipelines);
        var defaultPipeline = await db.Set<CustomerPipeline>()
            .SingleAsync(p => p.TenantId == Tenant && p.OrganizationId == Org && p.IsDefault);
        Assert.Equal("Default Pipeline", defaultPipeline.Name);
        Assert.Equal(8, await db.Set<CustomerPipelineStage>().CountAsync(s => s.PipelineId == defaultPipeline.Id));

        // Example dataset (setup.ts seedExamples): 3 companies / 6 people / 6 deals.
        Assert.Equal(3, await db.Set<CustomerEntity>().CountAsync(e => e.Kind == "company" && e.TenantId == Tenant && e.DeletedAt == null));
        Assert.Equal(6, await db.Set<CustomerEntity>().CountAsync(e => e.Kind == "person" && e.TenantId == Tenant && e.DeletedAt == null));
        Assert.Equal(6, await db.Set<CustomerDeal>().CountAsync(d => d.TenantId == Tenant && d.DeletedAt == null));
        Assert.True(seeded >= 15); // 3 companies + 6 people + 6 deals

        // Deals are linked to their company and to their listed participants, and mapped onto a pipeline stage.
        Assert.Equal(6, await db.Set<CustomerDealCompanyLink>().CountAsync());
        Assert.True(await db.Set<CustomerDealPersonLink>().CountAsync() >= 6);
        Assert.True(await db.Set<CustomerDeal>().AllAsync(d => d.PipelineId == defaultPipeline.Id));
        Assert.Contains(await db.Set<CustomerDeal>().Select(d => d.Title).ToListAsync(),
            t => t == "Redwood Residences Solar Rollout");

        // Idempotent: a second full run adds nothing.
        await CustomersSeeder.SeedDefaultsAsync(db, registry, Tenant, Org);
        await CustomersSeeder.SeedExamplesAsync(db, registry, indexer, Tenant, Org);
        Assert.Equal(1, await db.Set<CustomerPipeline>().CountAsync(p => p.TenantId == Tenant && p.OrganizationId == Org));
        Assert.Equal(8, await db.Set<CustomerPipelineStage>().CountAsync(s => s.PipelineId == defaultPipeline.Id));
        Assert.Equal(6, await db.Set<CustomerDeal>().CountAsync(d => d.TenantId == Tenant && d.DeletedAt == null));
        Assert.Equal(6, await db.Set<CustomerDealCompanyLink>().CountAsync());
    }
}
