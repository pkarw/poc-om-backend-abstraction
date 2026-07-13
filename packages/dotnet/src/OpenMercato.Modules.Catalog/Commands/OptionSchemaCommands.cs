using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// <c>catalog.optionSchemas.create</c> — inserts a <c>catalog_product_option_schemas</c> row (a reusable
/// product option-schema template). Indexed as <c>catalog:catalog_option_schema_template</c>. Undoable
/// (soft-delete). Returns <c>{ id }</c>.
/// </summary>
public sealed class CreateOptionSchemaCommand
    : ICommand<OptionSchemaCreateInput, OptionSchemaResult>,
      ICommandLogMetadataBuilder<OptionSchemaCreateInput, OptionSchemaResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.optionSchemas.create";

    public async Task<OptionSchemaResult> ExecuteAsync(OptionSchemaCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var template = new CatalogOptionSchemaTemplate
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Name = CatalogHttp.Str(input.Body, "name")?.Trim() ?? string.Empty,
            // code is UNIQUE per scope; default to the slugified name when omitted (upstream slug default).
            Code = CatalogHttp.Str(input.Body, "code")?.Trim() is { Length: > 0 } c
                ? c
                : CatalogWriteHelpers.Slugify(CatalogHttp.Str(input.Body, "name") ?? string.Empty),
            Schema = CatalogHttp.RawJson(input.Body, "schema"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        CatalogWriteHelpers.ApplyOptionSchemaBase(template, input.Body, isCreate: true);
        db.Set<CatalogOptionSchemaTemplate>().Add(template);
        await db.SaveChangesAsync();
        return new OptionSchemaResult(template.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OptionSchemaCreateInput input, OptionSchemaResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create option schema",
        ResourceKind = "catalog.option_schema",
        ResourceId = result.SchemaId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, true);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => SetDeletedAsync(log, services, false);

    private static async Task SetDeletedAsync(ActionLog log, IServiceProvider services, bool deleted)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var s = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(x => x.Id == id);
        if (s is not null) s.DeletedAt = deleted ? DateTimeOffset.UtcNow : null;
    }
}

/// <summary><c>catalog.optionSchemas.update</c> — patches the template; optimistic-locked; before/after
/// snapshots. Undoable.</summary>
public sealed class UpdateOptionSchemaCommand
    : ICommand<OptionSchemaUpdateInput, OptionSchemaResult>,
      ICommandLogMetadataBuilder<OptionSchemaUpdateInput, OptionSchemaResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.optionSchemas.update";
    private OptionSchemaSnapshot? _before;
    private OptionSchemaSnapshot? _after;

    public async Task<OptionSchemaResult> ExecuteAsync(OptionSchemaUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var template = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(s => s.Id == input.Id && s.DeletedAt == null);
        if (template is null) throw CommandHttpException.NotFound("Option schema not found");
        if (ctx.TenantId is { } tenant && template.TenantId != tenant) throw CommandHttpException.NotFound("Option schema not found");

        OptimisticLock.Enforce("catalog.option_schema", template.Id.ToString(), template.UpdatedAt.UtcDateTime, ctx);
        _before = CatalogWriteHelpers.Snapshot(template);
        CatalogWriteHelpers.ApplyOptionSchemaBase(template, input.Body, isCreate: false);
        template.UpdatedAt = DateTimeOffset.UtcNow;
        _after = CatalogWriteHelpers.Snapshot(template);
        await db.SaveChangesAsync();
        return new OptionSchemaResult(template.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OptionSchemaUpdateInput input, OptionSchemaResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update option schema",
        ResourceKind = "catalog.option_schema",
        ResourceId = result.SchemaId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotBefore<OptionSchemaSnapshot>());
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services, log.GetSnapshotAfter<OptionSchemaSnapshot>());

    private static async Task RestoreAsync(ActionLog log, IServiceProvider services, OptionSchemaSnapshot? s)
    {
        if (s is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var t = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return;
        t.Name = s.Name; t.Code = s.Code ?? t.Code; t.Description = s.Description; t.IsActive = s.IsActive;
        t.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary><c>catalog.optionSchemas.delete</c> — soft-deletes the template. Undoable.</summary>
public sealed class DeleteOptionSchemaCommand
    : ICommand<OptionSchemaDeleteInput, OptionSchemaResult>,
      ICommandLogMetadataBuilder<OptionSchemaDeleteInput, OptionSchemaResult>,
      IUndoableCommand
{
    public string CommandId => "catalog.optionSchemas.delete";

    public async Task<OptionSchemaResult> ExecuteAsync(OptionSchemaDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var template = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(s => s.Id == input.Id && s.DeletedAt == null);
        if (template is null) throw CommandHttpException.NotFound("Option schema not found");
        if (ctx.TenantId is { } tenant && template.TenantId != tenant) throw CommandHttpException.NotFound("Option schema not found");

        template.DeletedAt = DateTimeOffset.UtcNow;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new OptionSchemaResult(template.Id.ToString());
    }

    public CommandLogMetadata BuildLog(OptionSchemaDeleteInput input, OptionSchemaResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete option schema",
        ResourceKind = "catalog.option_schema",
        ResourceId = result.SchemaId,
        TenantId = ctx.TenantId,
        OrganizationId = ctx.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var s = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(x => x.Id == id);
        if (s is not null) { s.DeletedAt = null; s.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var s = await db.Set<CatalogOptionSchemaTemplate>().FirstOrDefaultAsync(x => x.Id == id);
        if (s is not null) { s.DeletedAt = DateTimeOffset.UtcNow; s.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
