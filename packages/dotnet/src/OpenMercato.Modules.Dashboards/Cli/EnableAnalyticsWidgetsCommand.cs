using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dashboards.Lib;
using OpenMercato.Modules.Dashboards.Seeding;

namespace OpenMercato.Modules.Dashboards.Cli;

/// <summary>
/// <c>enable-analytics-widgets</c> — port of upstream dashboards cli.ts enable-analytics-widgets.
/// Appends the analytics widget ids to the given roles' existing records (append-only).
/// Usage: enable-analytics-widgets --tenant &lt;id&gt; [--org &lt;id&gt;] [--roles admin,employee]
/// </summary>
public sealed class EnableAnalyticsWidgetsCommand : ICliCommand
{
    public string Name => "enable-analytics-widgets";
    public string Description => "Append analytics widgets to roles: --tenant <id> [--org <id>] [--roles admin,employee]";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var tenantRaw = parsed.Get("tenant", "tenantId");
        if (tenantRaw is null || !Guid.TryParse(tenantRaw, out var tenantId))
        {
            Console.Error.WriteLine("Usage: dashboards enable-analytics-widgets --tenant <tenantId> [--org <id>] [--roles admin,employee]");
            return 1;
        }
        Guid? organizationId = Guid.TryParse(parsed.Get("org", "organization", "organizationId") ?? "", out var org) ? org : null;
        var roleNames = (parsed.Get("roles") ?? "admin,employee")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ok = await DashboardSeeder.AppendWidgetsToRolesAsync(db, tenantId, organizationId, roleNames, WidgetCatalog.AnalyticsWidgetIds());
        Console.WriteLine(ok ? $"Enabled analytics widgets for tenant {tenantId}." : "No role records updated.");
        return 0;
    }
}
