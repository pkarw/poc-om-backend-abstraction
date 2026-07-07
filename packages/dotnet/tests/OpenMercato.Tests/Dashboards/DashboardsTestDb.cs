using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Dashboards;

namespace OpenMercato.Tests.Dashboards;

/// <summary>Builds an in-memory <see cref="AppDbContext"/> wired with the auth + dashboards models
/// (dashboards resolution needs auth's User/Role/UserRole entities).</summary>
internal static class DashboardsTestDb
{
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"dash-tests-{Guid.NewGuid():N}")
            .Options;
        var registry = new ModuleRegistry(new IModule[] { new AuthModule(), new DashboardsModule() });
        return new AppDbContext(options, registry);
    }
}
