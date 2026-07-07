using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenMercato.Core.Configuration;
using OpenMercato.Core.Data;

namespace OpenMercato.Api;

/// <summary>
/// Used by the `dotnet ef` CLI (make migrate, dotnet ef migrations add ...).
/// Builds the context with the full module catalog so the design-time model
/// includes every module's entities.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        DotEnv.Load();
        var config = AppConfig.FromEnvironment();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(config.NpgsqlConnectionString, npgsql => npgsql.MigrationsAssembly("OpenMercato.Api"))
            .Options;
        return new AppDbContext(options, ModuleCatalog.CreateRegistry());
    }
}
