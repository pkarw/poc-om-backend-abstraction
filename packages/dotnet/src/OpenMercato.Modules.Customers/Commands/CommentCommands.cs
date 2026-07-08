using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// <c>customers.comments.create</c> — inserts a <c>customer_comments</c> row on an entity (optionally a
/// deal). Returns <c>{ commentId, authorUserId }</c>. Undoable. Index + <c>customers.comment.created</c>
/// event are emitted by the CRUD factory (this route is factory-backed), so the command owns persistence only.
/// </summary>
public sealed class CreateCommentCommand
    : ICommand<CommentCreateInput, CommentResult>,
      ICommandLogMetadataBuilder<CommentCreateInput, CommentResult>,
      IUndoableCommand
{
    public string CommandId => "customers.comments.create";

    public async Task<CommentResult> ExecuteAsync(CommentCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var b = input.Body;
        var entityId = J.GuidOf(b, "entityId") ?? Guid.Empty;

        var parent = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == entityId && e.DeletedAt == null && e.TenantId == input.TenantId);
        if (parent is null) throw CommandHttpException.NotFound("Customer not found");

        var dealId = J.GuidOf(b, "dealId");
        if (dealId is { } d)
        {
            var deal = await db.Set<CustomerDeal>().AnyAsync(x => x.Id == d && x.DeletedAt == null && x.TenantId == input.TenantId);
            if (!deal) throw CommandHttpException.NotFound("Deal not found");
        }

        var now = DateTimeOffset.UtcNow;
        var comment = new CustomerComment
        {
            Id = Guid.NewGuid(),
            OrganizationId = parent.OrganizationId,
            TenantId = parent.TenantId,
            EntityId = parent.Id,
            DealId = dealId,
            Body = J.Str(b, "body")?.Trim() ?? string.Empty,
            AuthorUserId = J.GuidOf(b, "authorUserId") ?? ctx.UserId,
            AppearanceIcon = J.Str(b, "appearanceIcon"),
            AppearanceColor = J.Str(b, "appearanceColor"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CustomerComment>().Add(comment);
        return new CommentResult(comment.Id.ToString(), comment.AuthorUserId?.ToString());
    }

    public CommandLogMetadata BuildLog(CommentCreateInput input, CommentResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create note",
        ResourceKind = "customers.comment",
        ResourceId = result.CommentId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (c is not null) db.Set<CustomerComment>().Remove(c);
    }

    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.comments.update</c> — updates provided fields on a <c>customer_comments</c> row.</summary>
public sealed class UpdateCommentCommand
    : ICommand<CommentUpdateInput, CommentResult>,
      ICommandLogMetadataBuilder<CommentUpdateInput, CommentResult>,
      IUndoableCommand
{
    public string CommandId => "customers.comments.update";
    private CommentSnapshot? _before;

    public async Task<CommentResult> ExecuteAsync(CommentUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (c is null) throw CommandHttpException.NotFound("Comment not found");
        if (ctx.TenantId is { } t && c.TenantId != t) throw CommandHttpException.NotFound("Comment not found");
        OptimisticLock.Enforce("customers.comment", c.Id.ToString(), c.UpdatedAt.UtcDateTime, ctx);
        _before = Of(c);

        var b = input.Body;
        if (J.Has(b, "body")) c.Body = J.Str(b, "body")?.Trim() ?? c.Body;
        if (J.Has(b, "appearanceIcon")) c.AppearanceIcon = J.Str(b, "appearanceIcon");
        if (J.Has(b, "appearanceColor")) c.AppearanceColor = J.Str(b, "appearanceColor");
        if (J.Has(b, "dealId")) c.DealId = J.GuidOf(b, "dealId");
        c.UpdatedAt = DateTimeOffset.UtcNow;
        return new CommentResult(c.Id.ToString(), c.AuthorUserId?.ToString());
    }

    public CommandLogMetadata BuildLog(CommentUpdateInput input, CommentResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update note",
        ResourceKind = "customers.comment",
        ResourceId = result.CommentId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var snap = log.GetSnapshotBefore<CommentSnapshot>();
        if (snap is null) return;
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == snap.Id);
        if (c is null) return;
        c.Body = snap.Body; c.AppearanceIcon = snap.AppearanceIcon; c.AppearanceColor = snap.AppearanceColor;
        c.DealId = snap.DealId; c.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;

    internal static CommentSnapshot Of(CustomerComment c) => new(
        c.Id, c.OrganizationId, c.TenantId, c.EntityId, c.DealId, c.Body, c.AuthorUserId,
        c.AppearanceIcon, c.AppearanceColor, c.CreatedAt, c.UpdatedAt, c.DeletedAt);
}

/// <summary><c>customers.comments.delete</c> — soft-deletes a <c>customer_comments</c> row. Undoable.</summary>
public sealed class DeleteCommentCommand
    : ICommand<CommentDeleteInput, CommentResult>,
      ICommandLogMetadataBuilder<CommentDeleteInput, CommentResult>,
      IUndoableCommand
{
    public string CommandId => "customers.comments.delete";
    private CommentSnapshot? _before;

    public async Task<CommentResult> ExecuteAsync(CommentDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (c is null) throw CommandHttpException.NotFound("Comment not found");
        if (ctx.TenantId is { } t && c.TenantId != t) throw CommandHttpException.NotFound("Comment not found");
        OptimisticLock.Enforce("customers.comment", c.Id.ToString(), c.UpdatedAt.UtcDateTime, ctx);
        _before = UpdateCommentCommand.Of(c);
        c.DeletedAt = DateTimeOffset.UtcNow;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        return new CommentResult(c.Id.ToString(), c.AuthorUserId?.ToString());
    }

    public CommandLogMetadata BuildLog(CommentDeleteInput input, CommentResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete note",
        ResourceKind = "customers.comment",
        ResourceId = result.CommentId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (c is not null) { c.DeletedAt = null; c.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var c = await db.Set<CustomerComment>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (c is not null) { c.DeletedAt = DateTimeOffset.UtcNow; c.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
