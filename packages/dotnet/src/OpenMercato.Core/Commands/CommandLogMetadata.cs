namespace OpenMercato.Core.Commands;

/// <summary>
/// Metadata a command contributes for its action-log row — the port of upstream
/// <c>CommandLogMetadata</c> (packages/shared/src/lib/commands/types.ts). Returned from
/// <see cref="ICommandLogMetadataBuilder{TInput,TResult}.BuildLog"/>. All fields optional; the
/// <see cref="CommandBus"/> fills tenant/org/actor from <see cref="CommandContext"/> when omitted and
/// serializes the snapshot/changes/context objects to jsonb.
/// </summary>
public sealed record CommandLogMetadata
{
    /// <summary>When true, no action-log row is written (upstream <c>skipLog</c>).</summary>
    public bool SkipLog { get; init; }

    public string? ActionLabel { get; init; }
    public string? ResourceKind { get; init; }
    public string? ResourceId { get; init; }
    public string? ParentResourceKind { get; init; }
    public string? ParentResourceId { get; init; }
    public string? RelatedResourceKind { get; init; }
    public string? RelatedResourceId { get; init; }

    /// <summary>Before-snapshot object; serialized to <c>snapshot_before</c> jsonb.</summary>
    public object? SnapshotBefore { get; init; }

    /// <summary>After-snapshot object; serialized to <c>snapshot_after</c> jsonb.</summary>
    public object? SnapshotAfter { get; init; }

    /// <summary>Explicit field changes; serialized to <c>changes_json</c> jsonb.</summary>
    public object? Changes { get; init; }

    /// <summary>Free-form context; serialized to <c>context_json</c> jsonb.</summary>
    public object? Context { get; init; }

    /// <summary>Override tenant scope for the row (defaults to <see cref="CommandContext.TenantId"/>).</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Override org scope for the row (defaults to <see cref="CommandContext.OrganizationId"/>).</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Override actor for the row (defaults to <see cref="CommandContext.UserId"/>).</summary>
    public Guid? ActorUserId { get; init; }
}

/// <summary>Result of <see cref="CommandBus.ExecuteWithLog{TInput,TResult}"/>: the command result plus
/// the persisted action-log row (null when the command opted out via <see cref="CommandLogMetadata.SkipLog"/>
/// or is not undoable and contributed no metadata). Mirrors upstream <c>CommandExecuteResult</c>.</summary>
public sealed record CommandExecuteResult<TResult>(TResult Result, ActionLog? LogEntry);
