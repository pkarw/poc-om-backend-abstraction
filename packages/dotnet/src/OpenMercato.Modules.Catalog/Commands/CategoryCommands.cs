using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;
using OpenMercato.Modules.Catalog.Lib;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// Category write helpers: slug normalization + uniqueness, parent-scope validation, and the
/// materialized-path rebuild after every mutation (upstream commands/categories.ts).
/// </summary>
internal static class CategoryWrite
{
    public static string? NormalizeSlug(string? slug)
    {
        var trimmed = slug?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Ensure a category with this slug does not already exist in scope (excluding <paramref name="excludeId"/>).</summary>
    public static async Task AssertSlugFreeAsync(AppDbContext db, string? slug, Guid org, Guid tenant, Guid? excludeId)
    {
        if (string.IsNullOrEmpty(slug)) return;
        var conflict = await db.Set<CatalogProductCategory>().AnyAsync(c =>
            c.Slug == slug && c.OrganizationId == org && c.TenantId == tenant && c.DeletedAt == null &&
            (excludeId == null || c.Id != excludeId));
        if (conflict) throw CommandHttpException.BadRequest("Category slug already exists for this organization.");
    }

    /// <summary>Resolve + validate the requested parent id: must exist in scope (else 400). Null clears.</summary>
    public static async Task<Guid?> ResolveParentAsync(AppDbContext db, Guid? parentId, Guid org, Guid tenant)
    {
        if (parentId is null) return null;
        var exists = await db.Set<CatalogProductCategory>().AnyAsync(c =>
            c.Id == parentId && c.OrganizationId == org && c.TenantId == tenant && c.DeletedAt == null);
        if (!exists) throw CommandHttpException.BadRequest("Parent category not found or inaccessible.");
        return parentId;
    }
}

/// <summary>
/// <c>catalog.categories.create</c> — inserts a category then rebuilds the org's materialized-path tree.
/// Validates slug uniqueness and parent scope. Undoable (soft-delete + rebuild). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreateCategoryCommand
    : ICommand<CategoryCreateInput, CategoryResult>,
      ICommandLogMetadataBuilder<CategoryCreateInput, CategoryResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.categories.create";

    public async Task<CategoryResult> ExecuteAsync(CategoryCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var slug = CategoryWrite.NormalizeSlug(CatalogHttp.Str(body, "slug"));
        await CategoryWrite.AssertSlugFreeAsync(db, slug, input.OrganizationId, input.TenantId, null);
        var parentId = await CategoryWrite.ResolveParentAsync(db, CatalogHttp.GuidOf(body, "parentId"), input.OrganizationId, input.TenantId);

        var now = DateTimeOffset.UtcNow;
        var description = CatalogHttp.Str(body, "description")?.Trim();
        var category = new CatalogProductCategory
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Name = CatalogHttp.Str(body, "name")?.Trim() ?? string.Empty,
            Slug = slug,
            Description = string.IsNullOrEmpty(description) ? null : description,
            ParentId = parentId,
            IsActive = CatalogHttp.Bool(body, "isActive") != false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CatalogProductCategory>().Add(category);
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, input.OrganizationId, input.TenantId);
        return new CategoryResult(category.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CategoryCreateInput input, CategoryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create catalog category",
        ResourceKind = "catalog.category",
        ResourceId = result.CategoryId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, deleted: true);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, deleted: false);

    private static async Task SetDeletedAsync(ActionLog log, IServiceProvider services, bool deleted)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var c = await db.Set<CatalogProductCategory>().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return;
        c.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
        if (deleted) c.IsActive = false;
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, c.OrganizationId, c.TenantId);
    }
}

/// <summary><c>catalog.categories.update</c> — patches name/slug/description/parentId/isActive then
/// rebuilds the tree; before/after snapshots for the changelog diff. Undoable.</summary>
public sealed class UpdateCategoryCommand
    : ICommand<CategoryUpdateInput, CategoryResult>,
      ICommandLogMetadataBuilder<CategoryUpdateInput, CategoryResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.categories.update";
    private CategorySnapshot? _before;
    private CategorySnapshot? _after;

    public async Task<CategoryResult> ExecuteAsync(CategoryUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var category = await db.Set<CatalogProductCategory>().FirstOrDefaultAsync(c => c.Id == input.Id && c.DeletedAt == null);
        if (category is null) throw CommandHttpException.NotFound("Catalog category not found");
        if (ctx.TenantId is { } tenant && category.TenantId != tenant) throw CommandHttpException.NotFound("Catalog category not found");

        OptimisticLock.Enforce("catalog.category", category.Id.ToString(), category.UpdatedAt.UtcDateTime, ctx);
        _before = Snapshot(category);

        if (CatalogHttp.Has(body, "name"))
        {
            var name = CatalogHttp.Str(body, "name")?.Trim();
            if (!string.IsNullOrEmpty(name)) category.Name = name;
        }
        if (CatalogHttp.Has(body, "slug"))
        {
            var slug = CategoryWrite.NormalizeSlug(CatalogHttp.Str(body, "slug"));
            if (slug != category.Slug)
            {
                await CategoryWrite.AssertSlugFreeAsync(db, slug, category.OrganizationId, category.TenantId, category.Id);
                category.Slug = slug;
            }
        }
        if (CatalogHttp.Has(body, "description"))
        {
            var description = CatalogHttp.Str(body, "description")?.Trim();
            category.Description = string.IsNullOrEmpty(description) ? null : description;
        }
        if (CatalogHttp.Has(body, "parentId"))
        {
            var requested = CatalogHttp.GuidOf(body, "parentId");
            // A category cannot be its own parent (upstream safeParent); such a request clears the parent.
            var safeParent = requested is { } r && r != category.Id ? requested : null;
            category.ParentId = await CategoryWrite.ResolveParentAsync(db, safeParent, category.OrganizationId, category.TenantId);
        }
        if (CatalogHttp.Has(body, "isActive"))
            category.IsActive = CatalogHttp.Bool(body, "isActive") ?? category.IsActive;

        category.UpdatedAt = DateTimeOffset.UtcNow;
        _after = Snapshot(category);
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, category.OrganizationId, category.TenantId);
        return new CategoryResult(category.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CategoryUpdateInput input, CategoryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update catalog category",
        ResourceKind = "catalog.category",
        ResourceId = result.CategoryId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<CategorySnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<CategorySnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, CategorySnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var c = await db.Set<CatalogProductCategory>().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return;
        c.Name = s.Name; c.Slug = s.Slug; c.Description = s.Description;
        c.ParentId = Guid.TryParse(s.ParentId, out var p) ? p : null;
        c.IsActive = s.IsActive;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, c.OrganizationId, c.TenantId);
    }

    internal static CategorySnapshot Snapshot(CatalogProductCategory c) =>
        new(c.Name, c.Slug, c.Description, c.ParentId?.ToString(), c.IsActive);
}

/// <summary><c>catalog.categories.delete</c> — soft-deletes the category (isActive=false) then rebuilds
/// the tree. Undoable.</summary>
public sealed class DeleteCategoryCommand
    : ICommand<CategoryDeleteInput, CategoryResult>,
      ICommandLogMetadataBuilder<CategoryDeleteInput, CategoryResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.categories.delete";
    private CategorySnapshot? _before;

    public async Task<CategoryResult> ExecuteAsync(CategoryDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var category = await db.Set<CatalogProductCategory>().FirstOrDefaultAsync(c => c.Id == input.Id && c.DeletedAt == null);
        if (category is null) throw CommandHttpException.NotFound("Catalog category not found");
        if (ctx.TenantId is { } tenant && category.TenantId != tenant) throw CommandHttpException.NotFound("Catalog category not found");

        _before = UpdateCategoryCommand.Snapshot(category);
        category.DeletedAt = DateTimeOffset.UtcNow;
        category.IsActive = false;
        category.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, category.OrganizationId, category.TenantId);
        return new CategoryResult(category.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CategoryDeleteInput input, CategoryResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete catalog category",
        ResourceKind = "catalog.category",
        ResourceId = result.CategoryId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, deleted: false);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, deleted: true);

    private static async Task SetDeletedAsync(ActionLog log, IServiceProvider services, bool deleted)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var c = await db.Set<CatalogProductCategory>().FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return;
        c.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
        if (deleted) c.IsActive = false;
        else if (log.GetSnapshotBefore<CategorySnapshot>() is { } before) c.IsActive = before.IsActive;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CategoryHierarchy.RebuildAsync(db, c.OrganizationId, c.TenantId);
    }
}
