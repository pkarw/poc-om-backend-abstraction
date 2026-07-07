using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace OpenMercato.Core.Modules;

/// <summary>
/// Ordered collection of registered modules. Upstream discovers modules from the
/// filesystem at build time; here registration is explicit in each host's
/// composition root (see OpenMercato.Api/ModuleCatalog.cs).
/// </summary>
public sealed class ModuleRegistry
{
    public ModuleRegistry(IEnumerable<IModule> modules)
    {
        Modules = modules.ToList();
        var duplicate = Modules.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate module id registered: '{duplicate.Key}'.");
    }

    public IReadOnlyList<IModule> Modules { get; }

    public IReadOnlyList<string> AclFeatures =>
        Modules.SelectMany(m => m.AclFeatures).Distinct().ToList();

    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var module in Modules) module.ConfigureServices(services);
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        foreach (var module in Modules) module.ConfigureModel(modelBuilder);
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        foreach (var module in Modules) module.MapRoutes(routes);
    }
}
