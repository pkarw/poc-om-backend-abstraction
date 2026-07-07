using Microsoft.EntityFrameworkCore;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _modules.ConfigureModel(modelBuilder);
    }
}
