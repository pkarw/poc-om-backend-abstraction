using System.Text.Json;

namespace OpenMercato.Core.Commands;

/// <summary>
/// The persisted action-log row (table <c>action_logs</c>) — the port of upstream
/// <c>packages/core/src/modules/audit_logs/data/entities.ts</c> (class <c>ActionLog</c>).
///
/// Every undoable write through the <see cref="CommandBus"/> persists one row here with a generated
/// <see cref="UndoToken"/>, the redo envelope (<c>{ "__redoInput": &lt;input&gt; }</c>) under
/// <see cref="CommandPayload"/>, and before/after snapshots. Undo/redo transition
/// <see cref="ExecutionState"/> atomically (done → undoing → undone → done …).
///
/// jsonb columns are stored as raw JSON strings; use <see cref="GetRedoInput{T}"/> /
/// <see cref="GetSnapshotBefore{T}"/> / <see cref="GetSnapshotAfter{T}"/> to deserialize inside an
/// undo/redo handler. Column names/types are byte-identical to upstream (shared-DB contract, spec 03).
/// </summary>
public sealed class ActionLog
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }

    public string CommandId { get; set; } = string.Empty;
    public string? ActionLabel { get; set; }
    public string? ActionType { get; set; }
    public string? ResourceKind { get; set; }
    public string? ResourceId { get; set; }
    public string? ParentResourceKind { get; set; }
    public string? ParentResourceId { get; set; }

    /// <summary>done | undoing | undone | redoing | failed. Default 'done'.</summary>
    public string ExecutionState { get; set; } = "done";

    public string? UndoToken { get; set; }

    /// <summary>jsonb: the redo envelope <c>{ "__redoInput": &lt;input&gt;, ... }</c> (raw JSON).</summary>
    public string? CommandPayload { get; set; }

    /// <summary>jsonb: before-snapshot (raw JSON).</summary>
    public string? SnapshotBefore { get; set; }

    /// <summary>jsonb: after-snapshot (raw JSON).</summary>
    public string? SnapshotAfter { get; set; }

    /// <summary>jsonb: auto-diffed field changes (raw JSON).</summary>
    public string? ChangesJson { get; set; }

    /// <summary>text[]: changed field names (audit_logs projection — PARITY-TODO, left null here).</summary>
    public string[]? ChangedFields { get; set; }

    public string? PrimaryChangedField { get; set; }

    /// <summary>jsonb: free-form context (raw JSON).</summary>
    public string? ContextJson { get; set; }

    public string? SourceKey { get; set; }
    public string? RelatedResourceKind { get; set; }
    public string? RelatedResourceId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Deserialize the original command input from the <c>__redoInput</c> redo envelope.</summary>
    public T? GetRedoInput<T>()
    {
        if (string.IsNullOrEmpty(CommandPayload)) return default;
        using var doc = JsonDocument.Parse(CommandPayload);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("__redoInput", out var redo))
        {
            return redo.Deserialize<T>(JsonOptions);
        }
        return default;
    }

    /// <summary>Deserialize the before-snapshot.</summary>
    public T? GetSnapshotBefore<T>() => Deserialize<T>(SnapshotBefore);

    /// <summary>Deserialize the after-snapshot.</summary>
    public T? GetSnapshotAfter<T>() => Deserialize<T>(SnapshotAfter);

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
}
