using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Entities.Cli;

/// <summary>
/// <c>entities install-ce</c> — materializes every module's declared CE field sets
/// (<see cref="ModuleRegistry.AllCustomFieldSets"/>) into <c>custom_field_defs</c> rows for each tenant
/// (upstream <c>installCustomEntitiesFromModules</c>). Idempotent; safe to re-run. Optional
/// <c>--tenant &lt;uuid&gt;</c> restricts to one tenant, <c>--global</c> also installs global (null-scope) defs.
/// </summary>
public sealed class InstallCeCommand : ICliCommand
{
    public string Name => "install-ce";
    public string Description => "Install module-declared custom field sets into custom_field_defs (per tenant).";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();

        var tenantArg = parsed.Get("tenant", "tenantId");
        List<Guid> tenantIds;
        if (tenantArg is not null && Guid.TryParse(tenantArg, out var one))
            tenantIds = new List<Guid> { one };
        else
            tenantIds = await db.Database.SqlQueryRaw<Guid>("SELECT id FROM tenants WHERE deleted_at IS NULL").ToListAsync();

        var total = new InstallCeResult(0, 0, 0);
        if (parsed.Get("global") is not null)
            total += await InstallFromCe.InstallAsync(db, registry, tenantId: null, organizationId: null);
        foreach (var tenantId in tenantIds)
            total += await InstallFromCe.InstallAsync(db, registry, tenantId, organizationId: null);

        Console.WriteLine($"entities install-ce: tenants={tenantIds.Count} created={total.Created} updated={total.Updated} unchanged={total.Unchanged}");
        return 0;
    }
}
