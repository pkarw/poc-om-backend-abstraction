using System.Text.Json;

namespace OpenMercato.Modules.Customers.Commands;

// Command input/result contracts for the customers Phase-3b timeline write surface
// (interactions + comments). Create/update inputs carry the raw request body (JsonElement) so the
// handler can read fields AND persist cf_* custom-field values in the same transaction, exactly as the
// Phase-1 records do (Contracts.cs). New file — never edit the Phase-1 Contracts.cs.

// ---- Interactions ----------------------------------------------------------------------------
public sealed record InteractionCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record InteractionUpdateInput(Guid Id, JsonElement Body);
public sealed record InteractionDeleteInput(Guid Id);
public sealed record InteractionCompleteInput(Guid Id, DateTimeOffset? OccurredAt);
public sealed record InteractionCancelInput(Guid Id);

/// <summary>
/// Rich write result: carries everything the hand-written routes need to emit side effects
/// (index upsert + <c>customers.interaction.*</c> + <c>customers.next_interaction.updated</c> events)
/// without re-loading the row.
/// </summary>
public sealed record InteractionResult(
    string? InteractionId,
    Guid OrganizationId,
    Guid TenantId,
    Guid EntityId,
    string? NextInteractionId,
    string InteractionType,
    string Status,
    DateTimeOffset? OccurredAt,
    string? Source,
    DateTimeOffset? UpdatedAt = null);

// ---- Comments --------------------------------------------------------------------------------
public sealed record CommentCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record CommentUpdateInput(Guid Id, JsonElement Body);
public sealed record CommentDeleteInput(Guid Id);
public sealed record CommentResult(string? CommentId, string? AuthorUserId);

// ---- Undo/redo snapshots ---------------------------------------------------------------------
public sealed record InteractionSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, Guid EntityId, Guid? DealId, string InteractionType,
    string? Title, string? Body, string Status, DateTimeOffset? ScheduledAt, DateTimeOffset? OccurredAt,
    int? Priority, Guid? AuthorUserId, Guid? OwnerUserId, string? AppearanceIcon, string? AppearanceColor,
    string? Source, int? DurationMinutes, string? Location, bool? AllDay, string? RecurrenceRule,
    DateTimeOffset? RecurrenceEnd, string? Participants, int? ReminderMinutes, string? Visibility,
    string? LinkedEntities, string? GuestPermissions, bool Pinned, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record CommentSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, Guid EntityId, Guid? DealId, string Body,
    Guid? AuthorUserId, string? AppearanceIcon, string? AppearanceColor,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);
