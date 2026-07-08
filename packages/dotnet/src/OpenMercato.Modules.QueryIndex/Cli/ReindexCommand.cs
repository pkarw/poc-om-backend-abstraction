using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex.Cli;

/// <summary>
/// <c>query_index:reindex</c> — re-project storage-backed records into <c>entity_indexes</c> (upstream
/// <c>query_index</c> cli.ts <c>reindex</c>). <c>--entity &lt;module:entity&gt;</c> selects the entity type;
/// <c>--tenant &lt;uuid&gt;</c> scopes to one tenant (default: all). Best-effort synchronous walk — the
/// partitioned worker job is a PARITY-TODO seam.
/// </summary>
public sealed class ReindexCommand : ICliCommand
{
    public string Name => "query_index:reindex";
    public string Description => "Rebuild the query index for an entity type (--entity <module:entity> [--tenant <uuid>]).";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var entityType = parsed.Get("entity", "entityType");
        if (string.IsNullOrWhiteSpace(entityType) || !Reindexer.IsValidEntityIdShape(entityType))
        {
            Console.Error.WriteLine("query_index:reindex: --entity <module:entity> is required (e.g. --entity example:todo)");
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexer = scope.ServiceProvider.GetRequiredService<ICrudIndexer>();

        var tenantArg = parsed.Get("tenant", "tenantId");
        Guid? tenantId = tenantArg is not null && Guid.TryParse(tenantArg, out var t) ? t : null;

        var processed = await Reindexer.ReindexEntityAsync(db, indexer, entityType!, tenantId);
        Console.WriteLine($"query_index:reindex: entity={entityType} tenant={(tenantId?.ToString() ?? "*")} processed={processed}");
        return 0;
    }
}
