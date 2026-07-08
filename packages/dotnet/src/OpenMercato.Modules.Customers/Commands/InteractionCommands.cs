using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Lib;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// Shared helpers for the interaction command handlers — body → row mapping and snapshot capture/restore.
/// </summary>
internal static class InteractionWrite
{
    public const string EntityType = InteractionCompat.InteractionEntityType;

    /// <summary>Derive <c>scheduledAt</c> from a <c>date</c>(+optional <c>time</c>) pair (deriveScheduledAtFromDateTime).</summary>
    public static DateTimeOffset? DeriveScheduled(JsonElement body)
    {
        var date = J.Str(body, "date")?.Trim();
        if (string.IsNullOrEmpty(date)) return null;
        var time = J.Str(body, "time")?.Trim();
        var iso = string.IsNullOrEmpty(time) ? $"{date}T00:00:00" : $"{date}T{time}:00";
        return DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }

    public static InteractionSnapshot Of(CustomerInteraction i) => new(
        i.Id, i.OrganizationId, i.TenantId, i.EntityId, i.DealId, i.InteractionType, i.Title, i.Body, i.Status,
        i.ScheduledAt, i.OccurredAt, i.Priority, i.AuthorUserId, i.OwnerUserId, i.AppearanceIcon, i.AppearanceColor,
        i.Source, i.DurationMinutes, i.Location, i.AllDay, i.RecurrenceRule, i.RecurrenceEnd, i.Participants,
        i.ReminderMinutes, i.Visibility, i.LinkedEntities, i.GuestPermissions, i.Pinned, i.CreatedAt, i.UpdatedAt, i.DeletedAt);

    public static void Apply(CustomerInteraction i, InteractionSnapshot s)
    {
        i.OrganizationId = s.OrganizationId; i.TenantId = s.TenantId; i.EntityId = s.EntityId; i.DealId = s.DealId;
        i.InteractionType = s.InteractionType; i.Title = s.Title; i.Body = s.Body; i.Status = s.Status;
        i.ScheduledAt = s.ScheduledAt; i.OccurredAt = s.OccurredAt; i.Priority = s.Priority; i.AuthorUserId = s.AuthorUserId;
        i.OwnerUserId = s.OwnerUserId; i.AppearanceIcon = s.AppearanceIcon; i.AppearanceColor = s.AppearanceColor;
        i.Source = s.Source; i.DurationMinutes = s.DurationMinutes; i.Location = s.Location; i.AllDay = s.AllDay;
        i.RecurrenceRule = s.RecurrenceRule; i.RecurrenceEnd = s.RecurrenceEnd; i.Participants = s.Participants;
        i.ReminderMinutes = s.ReminderMinutes; i.Visibility = s.Visibility; i.LinkedEntities = s.LinkedEntities;
        i.GuestPermissions = s.GuestPermissions; i.Pinned = s.Pinned; i.CreatedAt = s.CreatedAt;
        i.UpdatedAt = s.UpdatedAt; i.DeletedAt = s.DeletedAt;
    }
}

/// <summary>
/// <c>customers.interactions.create</c> — inserts a <c>customer_interactions</c> row, persists cf under
/// <c>customers:customer_interaction</c>, and recomputes the parent entity's next-interaction projection.
/// Returns the rich <see cref="InteractionResult"/>. Undoable (soft-delete/restore).
/// </summary>
public sealed class CreateInteractionCommand
    : ICommand<InteractionCreateInput, InteractionResult>,
      ICommandLogMetadataBuilder<InteractionCreateInput, InteractionResult>,
      IUndoableCommand
{
    public string CommandId => "customers.interactions.create";

    public async Task<InteractionResult> ExecuteAsync(InteractionCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var b = input.Body;
        var entityId = J.GuidOf(b, "entityId") ?? Guid.Empty;

        var parent = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == entityId && e.DeletedAt == null && (ctx.TenantId == null || e.TenantId == ctx.TenantId));
        if (parent is null) throw CommandHttpException.NotFound("Customer not found");

        var now = DateTimeOffset.UtcNow;
        var scheduledAt = J.Has(b, "scheduledAt") ? CustomersHttp.Date(b, "scheduledAt") : InteractionWrite.DeriveScheduled(b);
        var interaction = new CustomerInteraction
        {
            Id = J.GuidOf(b, "id") ?? Guid.NewGuid(),
            OrganizationId = parent.OrganizationId,
            TenantId = parent.TenantId,
            EntityId = parent.Id,
            InteractionType = J.Str(b, "interactionType")?.Trim() ?? string.Empty,
            Title = J.Str(b, "title"),
            Body = J.Str(b, "body"),
            Status = J.Str(b, "status") ?? "planned",
            ScheduledAt = scheduledAt,
            OccurredAt = CustomersHttp.Date(b, "occurredAt"),
            Priority = J.Int(b, "priority"),
            AuthorUserId = J.GuidOf(b, "authorUserId") ?? ctx.UserId,
            OwnerUserId = J.GuidOf(b, "ownerUserId"),
            DealId = J.GuidOf(b, "dealId"),
            Source = J.Str(b, "source"),
            AppearanceIcon = J.Str(b, "appearanceIcon"),
            AppearanceColor = J.Str(b, "appearanceColor"),
            DurationMinutes = J.Int(b, "durationMinutes"),
            Location = J.Str(b, "location"),
            AllDay = J.Bool(b, "allDay"),
            RecurrenceRule = J.Str(b, "recurrenceRule"),
            RecurrenceEnd = CustomersHttp.Date(b, "recurrenceEnd"),
            Participants = InteractionCompat.RawJson(b, "participants"),
            ReminderMinutes = J.Int(b, "reminderMinutes"),
            Visibility = J.Str(b, "visibility"),
            LinkedEntities = InteractionCompat.RawJson(b, "linkedEntities"),
            GuestPermissions = InteractionCompat.RawJson(b, "guestPermissions"),
            Pinned = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CustomerInteraction>().Add(interaction);
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, InteractionWrite.EntityType, interaction.Id, b, ctx);

        var nextId = await InteractionCompat.RecomputeNextInteractionAsync(db, parent.Id);
        return new InteractionResult(interaction.Id.ToString(), interaction.OrganizationId, interaction.TenantId,
            parent.Id, nextId, interaction.InteractionType, interaction.Status, interaction.OccurredAt, interaction.Source, interaction.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(InteractionCreateInput input, InteractionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create interaction",
        ResourceKind = "customers.interaction",
        ResourceId = result.InteractionId,
        ParentResourceId = result.EntityId.ToString(),
        TenantId = result.TenantId,
        OrganizationId = result.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (i is null) return;
        i.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (i is null) return;
        i.DeletedAt = null;
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }
}

/// <summary><c>customers.interactions.update</c> — applies provided fields (email-visibility gated), cf,
/// optimistic lock, and recomputes next-interaction. Undoable (snapshot restore).</summary>
public sealed class UpdateInteractionCommand
    : ICommand<InteractionUpdateInput, InteractionResult>,
      ICommandLogMetadataBuilder<InteractionUpdateInput, InteractionResult>,
      IUndoableCommand
{
    public string CommandId => "customers.interactions.update";
    private InteractionSnapshot? _before;

    public async Task<InteractionResult> ExecuteAsync(InteractionUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var b = input.Body;
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (i is null) throw CommandHttpException.NotFound("Interaction not found");
        if (ctx.TenantId is { } t && i.TenantId != t) throw CommandHttpException.NotFound("Interaction not found");
        OptimisticLock.Enforce("customers.interaction", i.Id.ToString(), i.UpdatedAt.UtcDateTime, ctx);
        _before = InteractionWrite.Of(i);

        // Email-visibility gate: only the author may flip a private email's visibility (404, existence-masking).
        if (J.Has(b, "visibility") && i.InteractionType == "email")
        {
            var next = J.Str(b, "visibility");
            if ((next ?? null) != (i.Visibility ?? null) &&
                !InteractionCompat.CanChangeEmailVisibility(i.InteractionType, i.Visibility, next, i.AuthorUserId, ctx.UserId))
                throw CommandHttpException.NotFound("Email not found");
        }

        if (J.Has(b, "dealId")) i.DealId = J.GuidOf(b, "dealId");
        if (J.Has(b, "interactionType")) i.InteractionType = J.Str(b, "interactionType")?.Trim() ?? i.InteractionType;
        if (J.Has(b, "title")) i.Title = J.Str(b, "title");
        if (J.Has(b, "body")) i.Body = J.Str(b, "body");
        if (J.Has(b, "status")) i.Status = J.Str(b, "status") ?? i.Status;
        if (J.Has(b, "scheduledAt")) i.ScheduledAt = CustomersHttp.Date(b, "scheduledAt");
        else if ((J.Has(b, "date") || J.Has(b, "time"))) { var d = InteractionWrite.DeriveScheduled(b); if (d is not null) i.ScheduledAt = d; }
        if (J.Has(b, "occurredAt")) i.OccurredAt = CustomersHttp.Date(b, "occurredAt");
        if (J.Has(b, "priority")) i.Priority = J.Int(b, "priority");
        if (J.Has(b, "authorUserId")) i.AuthorUserId = J.GuidOf(b, "authorUserId");
        if (J.Has(b, "ownerUserId")) i.OwnerUserId = J.GuidOf(b, "ownerUserId");
        if (J.Has(b, "appearanceIcon")) i.AppearanceIcon = J.Str(b, "appearanceIcon");
        if (J.Has(b, "appearanceColor")) i.AppearanceColor = J.Str(b, "appearanceColor");
        if (J.Has(b, "pinned")) i.Pinned = J.Bool(b, "pinned") ?? i.Pinned;
        if (J.Has(b, "durationMinutes")) i.DurationMinutes = J.Int(b, "durationMinutes");
        if (J.Has(b, "location")) i.Location = J.Str(b, "location");
        if (J.Has(b, "allDay")) i.AllDay = J.Bool(b, "allDay");
        if (J.Has(b, "recurrenceRule")) i.RecurrenceRule = J.Str(b, "recurrenceRule");
        if (J.Has(b, "recurrenceEnd")) i.RecurrenceEnd = CustomersHttp.Date(b, "recurrenceEnd");
        if (J.Has(b, "participants")) i.Participants = InteractionCompat.RawJson(b, "participants");
        if (J.Has(b, "reminderMinutes")) i.ReminderMinutes = J.Int(b, "reminderMinutes");
        if (J.Has(b, "visibility")) i.Visibility = J.Str(b, "visibility");
        if (J.Has(b, "linkedEntities")) i.LinkedEntities = InteractionCompat.RawJson(b, "linkedEntities");
        if (J.Has(b, "guestPermissions")) i.GuestPermissions = InteractionCompat.RawJson(b, "guestPermissions");
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, InteractionWrite.EntityType, i.Id, b, ctx);

        var nextId = await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
        return new InteractionResult(i.Id.ToString(), i.OrganizationId, i.TenantId, i.EntityId, nextId,
            i.InteractionType, i.Status, i.OccurredAt, i.Source, i.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(InteractionUpdateInput input, InteractionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update interaction",
        ResourceKind = "customers.interaction",
        ResourceId = result.InteractionId,
        ParentResourceId = result.EntityId.ToString(),
        TenantId = result.TenantId,
        OrganizationId = result.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var snap = log.GetSnapshotBefore<InteractionSnapshot>();
        if (snap is null) return;
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == snap.Id);
        if (i is null) return;
        InteractionWrite.Apply(i, snap);
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }

    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.interactions.complete</c> — status='done', occurredAt=parsed??now. Emits
/// <c>customers.interaction.completed</c> (from the route). Undoable.</summary>
public sealed class CompleteInteractionCommand
    : ICommand<InteractionCompleteInput, InteractionResult>,
      ICommandLogMetadataBuilder<InteractionCompleteInput, InteractionResult>,
      IUndoableCommand
{
    public string CommandId => "customers.interactions.complete";
    private InteractionSnapshot? _before;

    public async Task<InteractionResult> ExecuteAsync(InteractionCompleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (i is null) throw CommandHttpException.NotFound("Interaction not found");
        if (ctx.TenantId is { } t && i.TenantId != t) throw CommandHttpException.NotFound("Interaction not found");
        OptimisticLock.Enforce("customers.interaction", i.Id.ToString(), i.UpdatedAt.UtcDateTime, ctx);
        _before = InteractionWrite.Of(i);

        i.Status = "done";
        i.OccurredAt = input.OccurredAt ?? DateTimeOffset.UtcNow;
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var nextId = await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
        return new InteractionResult(i.Id.ToString(), i.OrganizationId, i.TenantId, i.EntityId, nextId,
            i.InteractionType, i.Status, i.OccurredAt, i.Source, i.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(InteractionCompleteInput input, InteractionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Complete interaction",
        ResourceKind = "customers.interaction",
        ResourceId = result.InteractionId,
        ParentResourceId = result.EntityId.ToString(),
        TenantId = result.TenantId,
        OrganizationId = result.OrganizationId,
        SnapshotBefore = _before,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => RestoreAsync(log, services);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;

    internal static async Task RestoreAsync(ActionLog log, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var snap = log.GetSnapshotBefore<InteractionSnapshot>();
        if (snap is null) return;
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == snap.Id);
        if (i is null) return;
        InteractionWrite.Apply(i, snap);
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }
}

/// <summary><c>customers.interactions.cancel</c> — status='canceled'. Emits
/// <c>customers.interaction.canceled</c> (from the route). Undoable.</summary>
public sealed class CancelInteractionCommand
    : ICommand<InteractionCancelInput, InteractionResult>,
      ICommandLogMetadataBuilder<InteractionCancelInput, InteractionResult>,
      IUndoableCommand
{
    public string CommandId => "customers.interactions.cancel";
    private InteractionSnapshot? _before;

    public async Task<InteractionResult> ExecuteAsync(InteractionCancelInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (i is null) throw CommandHttpException.NotFound("Interaction not found");
        if (ctx.TenantId is { } t && i.TenantId != t) throw CommandHttpException.NotFound("Interaction not found");
        OptimisticLock.Enforce("customers.interaction", i.Id.ToString(), i.UpdatedAt.UtcDateTime, ctx);
        _before = InteractionWrite.Of(i);

        i.Status = "canceled";
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var nextId = await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
        return new InteractionResult(i.Id.ToString(), i.OrganizationId, i.TenantId, i.EntityId, nextId,
            i.InteractionType, i.Status, i.OccurredAt, i.Source, i.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(InteractionCancelInput input, InteractionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Cancel interaction",
        ResourceKind = "customers.interaction",
        ResourceId = result.InteractionId,
        ParentResourceId = result.EntityId.ToString(),
        TenantId = result.TenantId,
        OrganizationId = result.OrganizationId,
        SnapshotBefore = _before,
    };

    public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => CompleteInteractionCommand.RestoreAsync(log, services);
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.interactions.delete</c> — soft-deletes the row + recompute. Undoable.</summary>
public sealed class DeleteInteractionCommand
    : ICommand<InteractionDeleteInput, InteractionResult>,
      ICommandLogMetadataBuilder<InteractionDeleteInput, InteractionResult>,
      IUndoableCommand
{
    public string CommandId => "customers.interactions.delete";
    private InteractionSnapshot? _before;

    public async Task<InteractionResult> ExecuteAsync(InteractionDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (i is null) throw CommandHttpException.NotFound("Interaction not found");
        if (ctx.TenantId is { } t && i.TenantId != t) throw CommandHttpException.NotFound("Interaction not found");
        OptimisticLock.Enforce("customers.interaction", i.Id.ToString(), i.UpdatedAt.UtcDateTime, ctx);
        _before = InteractionWrite.Of(i);

        i.DeletedAt = DateTimeOffset.UtcNow;
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var nextId = await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
        return new InteractionResult(i.Id.ToString(), i.OrganizationId, i.TenantId, i.EntityId, nextId,
            i.InteractionType, i.Status, i.OccurredAt, i.Source, i.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(InteractionDeleteInput input, InteractionResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete interaction",
        ResourceKind = "customers.interaction",
        ResourceId = result.InteractionId,
        ParentResourceId = result.EntityId.ToString(),
        TenantId = result.TenantId,
        OrganizationId = result.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var snap = log.GetSnapshotBefore<InteractionSnapshot>();
        if (snap is null) return;
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == snap.Id);
        if (i is null) return;
        i.DeletedAt = null;
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var i = await db.Set<CustomerInteraction>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (i is null) return;
        i.DeletedAt = DateTimeOffset.UtcNow;
        i.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await InteractionCompat.RecomputeNextInteractionAsync(db, i.EntityId);
    }
}
