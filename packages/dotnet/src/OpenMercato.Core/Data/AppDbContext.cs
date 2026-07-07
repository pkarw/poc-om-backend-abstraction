using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenMercato.Core.Modules;

namespace OpenMercato.Core.Data;

/// <summary>
/// Single shared DbContext. Modules do not own a context; they contribute their
/// entity mappings through IModule.ConfigureModel (mirrors upstream where every
/// module's data/entities.ts feeds one MikroORM instance). Access module
/// entities with dbContext.Set&lt;TEntity&gt;().
/// </summary>
public sealed class AppDbContext : DbContext
{
    private readonly ModuleRegistry _modules;

    public AppDbContext(DbContextOptions<AppDbContext> options, ModuleRegistry modules)
        : base(options)
    {
        _modules = modules;
    }

    /// <summary>Exposed so the model cache key factory can vary the cached model per registry.</summary>
    internal string ModelCacheKey => _modules.ModelCacheKey;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // EF caches the built model keyed by context CLR type alone. Because every host/test wires a
        // DIFFERENT ModuleRegistry into the SAME AppDbContext type, the default cache would leak the
        // first-built model to every other registry (a registry with only the directory module would
        // then be missing auth's entities, and vice-versa). Key the cached model by the registry
        // signature as well so each distinct module set gets its own model.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, RegistryModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _modules.ConfigureModel(modelBuilder);
    }
}

/// <summary>Model cache key that incorporates the registry's module signature (see OnConfiguring).</summary>
internal sealed class RegistryModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var registryKey = context is AppDbContext app ? app.ModelCacheKey : string.Empty;
        return (context.GetType(), registryKey, designTime);
    }

    public object Create(DbContext context) => Create(context, false);
}
