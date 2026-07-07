using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;

namespace OpenMercato.Tests.Auth;

/// <summary>Builds an in-memory <see cref="AppDbContext"/> wired with the auth entity model.</summary>
internal static class AuthTestDb
{
    public static AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"auth-tests-{Guid.NewGuid():N}")
            .Options;
        var registry = new ModuleRegistry(new IModule[] { new AuthModule() });
        return new AppDbContext(options, registry);
    }
}
