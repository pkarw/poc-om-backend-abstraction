using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Events;
using OpenMercato.Core.Modules;
using OpenMercato.Core.Queue;
using OpenMercato.Modules.HealthCheck.Api;
using OpenMercato.Modules.HealthCheck.Data;
using OpenMercato.Modules.HealthCheck.Subscribers;
using OpenMercato.Modules.HealthCheck.Workers;

namespace OpenMercato.Modules.HealthCheck;

/// <summary>
/// Reference module. Mirrors upstream packages/core/src/modules/&lt;module&gt;/:
/// routes (api/), entity mapping (data/entities.ts), worker (workers/),
/// subscriber (subscribers/), ACL features (acl.ts), DI wiring (di.ts).
/// </summary>
public sealed class HealthCheckModule : IModule
{
    public string Id => "health_check";

    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "health_check.view",
    };

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, HealthCheckWorker>();
        services.AddSingleton<IEventSubscriber, HealthPingedSubscriber>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<HealthPing>();
        entity.ToTable("om_health_ping");
        entity.HasKey(x => x.Id).HasName("pk_om_health_ping");
        entity.Property(x => x.Id).HasColumnName("id");
        entity.Property(x => x.Source).HasColumnName("source").IsRequired();
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        HealthCheckEndpoints.Map(routes);
    }
}
