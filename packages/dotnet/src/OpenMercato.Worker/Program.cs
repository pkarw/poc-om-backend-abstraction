using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Events;
using OpenMercato.Core.Modules;
using OpenMercato.Core.Queue;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.HealthCheck;
using StackExchange.Redis;

DotEnv.Load();
var config = AppConfig.FromEnvironment();

// Keep this list in sync with OpenMercato.Api/ModuleCatalog.cs.
var registry = new ModuleRegistry(new IModule[]
{
    new HealthCheckModule(),
    new AuthModule(),
});

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(registry);
// The worker consumes the queue connection (QUEUE_REDIS_URL, defaults to REDIS_URL).
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(ConnectionStrings.FromRedisUrl(config.QueueRedisUrl)));
builder.Services.AddSingleton<IJobQueue, RedisJobQueue>();
builder.Services.AddSingleton<IEventBus, LocalEventBus>();
registry.ConfigureServices(builder.Services);
builder.Services.AddHostedService<QueueWorkerService>();

var host = builder.Build();
await host.RunAsync();
