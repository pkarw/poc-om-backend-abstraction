using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.HealthCheck;

namespace OpenMercato.Api;

/// <summary>
/// Composition root for module registration. Upstream discovers modules from the
/// filesystem; here every ported module is added to this list (and to the list
/// in OpenMercato.Worker/Program.cs). Order matters for route and model setup.
/// </summary>
public static class ModuleCatalog
{
    public static ModuleRegistry CreateRegistry() => new(new IModule[]
    {
        new HealthCheckModule(),
        new AuthModule(),
    });
}
