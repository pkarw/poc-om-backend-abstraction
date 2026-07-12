using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;

namespace OpenMercato.Modules.Catalog;

/// <summary>
/// The catalog module (upstream packages/core/src/modules/catalog) — products, variants, categories,
/// tags, prices, offers, price-kinds, option-schemas and unit-conversions. OM owns + migrates the
/// <c>catalog_*</c> schema (the testbench runs the .NET port migrations-off); <see cref="ConfigureModel"/>
/// wires only the runtime EF model. Ported incrementally — see MapRoutes for the live surface.
/// </summary>
public sealed class CatalogModule : IModule
{
    public string Id => "catalog";

    public IReadOnlyList<string> AclFeatures { get; } = Array.Empty<string>();

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
    }
}
