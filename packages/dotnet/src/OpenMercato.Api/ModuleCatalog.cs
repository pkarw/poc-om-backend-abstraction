using OpenMercato.Core.Commands;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.Currencies;
using OpenMercato.Modules.Customers;
using OpenMercato.Modules.Dashboards;
using OpenMercato.Modules.Dictionaries;
using OpenMercato.Modules.Directory;
using OpenMercato.Modules.Entities;
using OpenMercato.Modules.HealthCheck;
using OpenMercato.Modules.QueryIndex;

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
        // entities: custom-field engine — 6 EAV tables + the real ICrudCustomFields codec + install-from-CE.
        new EntitiesModule(),
        // query_index: hybrid read model — entity_indexes projection + the real ICrudIndexer/ICrudIndexQuery.
        new QueryIndexModule(),
        // currencies: currency reference data + exchange rates + ConvertToBase conversion (CRUD via factory).
        new CurrenciesModule(),
        // dictionaries: org-scoped enumerations (dictionaries + dictionary_entries); writes via command bus.
        new DictionariesModule(),
        // customers: CRM domain — 25 tables (people/companies/deals/interactions/...); routes added per phase.
        new CustomersModule(),
    });
}
