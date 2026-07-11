using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenMercato.Core.Crud;

/// <summary>
/// DI wiring for the CRUD factory extension points. Hosts call <see cref="AddOpenMercatoCrud"/> once
/// (before module <c>ConfigureServices</c>) to register the no-op defaults; the entities, query_index
/// and auth modules then override them with real implementations via a later <c>AddScoped</c>
/// (last registration wins on resolve).
/// </summary>
public static class CrudServiceCollectionExtensions
{
    public static IServiceCollection AddOpenMercatoCrud(this IServiceCollection services)
    {
        // No-op defaults — replaced when the entities / query_index modules land.
        services.TryAddScoped<ICrudCustomFields, NoopCrudCustomFields>();
        services.TryAddScoped<ICrudIndexer, NoopCrudIndexer>();
        services.TryAddScoped<ICrudIndexQuery, NoopCrudIndexQuery>();
        services.TryAddScoped<IEntityLookupQuery, NoopEntityLookupQuery>();
        // Fail-closed auth bridge — the Auth module registers the real ICrudRequestContext.
        services.TryAddScoped<ICrudRequestContext, DefaultCrudRequestContext>();
        return services;
    }
}
