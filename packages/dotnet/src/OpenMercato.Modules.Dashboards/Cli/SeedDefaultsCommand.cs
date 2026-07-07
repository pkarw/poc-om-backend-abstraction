using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Dashboards.Seeding;

namespace OpenMercato.Modules.Dashboards.Cli;

/// <summary>
/// <c>seed-defaults</c> — port of upstream dashboards cli.ts seed-defaults. Upserts
/// dashboard_role_widgets per role (admin/superadmin → all widgets, others → defaultEnabled).
/// Usage: seed-defaults --tenant &lt;id&gt; [--roles superadmin,admin,employee] [--widgets id1,id2]
/// </summary>
public sealed class SeedDefaultsCommand : ICliCommand
{
    public string Name => "seed-defaults";
    public string Description => "Seed dashboard widget availability per role: --tenant <id> [--roles ...] [--widgets ...]";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var tenantRaw = parsed.Get("tenant", "tenantId");
        if (tenantRaw is null || !Guid.TryParse(tenantRaw, out var tenantId))
        {
            Console.Error.WriteLine("Usage: dashboards seed-defaults --tenant <tenantId> [--roles superadmin,admin,employee] [--widgets id1,id2]");
            return 1;
        }
        Guid? organizationId = Guid.TryParse(parsed.Get("organization", "organizationId") ?? "", out var org) ? org : null;
        var roleNames = (parsed.Get("roles") ?? "superadmin,admin,employee")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (roleNames.Count == 0) { Console.WriteLine("No roles provided, nothing to seed."); return 0; }
        var widgetCsv = parsed.Get("widgets");
        var widgetIds = string.IsNullOrEmpty(widgetCsv)
            ? null
            : widgetCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ok = await DashboardSeeder.SeedDefaultsForTenantAsync(db, tenantId, organizationId, roleNames, widgetIds);
        Console.WriteLine(ok ? $"Seeded dashboard defaults for tenant {tenantId}." : "No widgets resolved for dashboard seeding.");
        return 0;
    }
}
