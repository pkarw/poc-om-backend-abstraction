using System.Text.Json;

namespace OpenMercato.Modules.Customers.Commands;

// Command input/result + undo-snapshot contracts for the Phase-3a deals + pipelines write surface.
// Create/update inputs carry the raw request body (JsonElement) so the handler can read fields AND
// persist cf_* custom-field values in the same transaction (parity with people/companies). Ids are
// strings on the wire.

// ---- Deals -----------------------------------------------------------------------------------
public sealed record DealCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record DealUpdateInput(Guid Id, JsonElement Body);
public sealed record DealDeleteInput(Guid Id);
public sealed record DealResult(string? DealId);

/// <summary>Full deal snapshot (base row + link ids + stage-transition rows) for undo/redo.</summary>
public sealed record DealSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string Title, string? Description, string Status,
    string? PipelineStage, Guid? PipelineId, Guid? PipelineStageId, decimal? ValueAmount, string? ValueCurrency,
    int? Probability, DateTimeOffset? ExpectedCloseAt, Guid? OwnerUserId, string? Source, string? ClosureOutcome,
    Guid? LossReasonId, string? LossNotes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    List<Guid> People, List<Guid> Companies, List<DealTransitionSnapshot> Transitions);

public sealed record DealTransitionSnapshot(
    Guid Id, Guid PipelineId, Guid StageId, string StageLabel, int StageOrder,
    DateTimeOffset TransitionedAt, Guid? TransitionedByUserId);

// ---- Pipelines -------------------------------------------------------------------------------
public sealed record PipelineCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PipelineUpdateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PipelineDeleteInput(Guid OrganizationId, Guid TenantId, Guid Id);
public sealed record PipelineResult(string? PipelineId);

// ---- Pipeline stages -------------------------------------------------------------------------
public sealed record PipelineStageCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PipelineStageUpdateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PipelineStageDeleteInput(Guid OrganizationId, Guid TenantId, Guid Id);
public sealed record PipelineStageReorderInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PipelineStageResult(string? StageId);
