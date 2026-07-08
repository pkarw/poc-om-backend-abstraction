using OpenMercato.Core.Commands;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Dashboards;
using OpenMercato.Modules.Directory;
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
        new DirectoryModule(),
        new DashboardsModule(),
        // audit_logs: maps action_logs + registers the CommandBus/ActionLogService (command-write infra).
        new AuditLogsModule(),
    });
}
