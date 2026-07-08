using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;

namespace OpenMercato.Core.Commands;

/// <summary>
/// Persistence + atomic state transitions for <see cref="ActionLog"/> rows — the port of upstream
/// <c>ActionLogService</c> (packages/core/src/modules/audit_logs/services/actionLogService.ts), trimmed
/// to the command-bus surface (log / lookup / claim / release / mark). List/projection/encryption
/// concerns belong to the audit_logs API module (PARITY-TODO).
///
/// <see cref="ClaimForUndoAsync"/> / <see cref="ClaimForRedoAsync"/> are compare-and-set transitions:
/// on a relational provider they run as a single conditional <c>UPDATE ... WHERE execution_state = …</c>
/// (upstream <c>nativeUpdate</c>) so two concurrent holders of the same token cannot both proceed; on
/// the in-memory provider (tests) they fall back to a load-check-set.
/// </summary>
public sealed class ActionLogService
{
    private readonly AppDbContext _db;

    public ActionLogService(AppDbContext db) => _db = db;

    private bool IsRelational =>
        _db.Database.ProviderName is not null &&
        _db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>Persist a new action-log row and return it.</summary>
    public async Task<ActionLog> LogAsync(ActionLog log, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (log.CreatedAt == default) log.CreatedAt = now;
        log.UpdatedAt = now;
        _db.Set<ActionLog>().Add(log);
        await _db.SaveChangesAsync(ct);
        return log;
    }

    public Task<ActionLog?> FindByUndoTokenAsync(string undoToken, CancellationToken ct = default) =>
        _db.Set<ActionLog>().FirstOrDefaultAsync(l => l.UndoToken == undoToken && l.DeletedAt == null, ct);

    public Task<ActionLog?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Set<ActionLog>().FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);

    /// <summary>Atomic CAS <c>done → undoing</c>. Returns true iff exactly one row transitioned.</summary>
    public Task<bool> ClaimForUndoAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, from: "done", to: "undoing", ct);

    /// <summary>Release a stranded claim: CAS <c>undoing → done</c> (undo failed after claiming).</summary>
    public Task<bool> ReleaseUndoClaimAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, from: "undoing", to: "done", ct);

    /// <summary>Atomic CAS <c>undone → redoing</c> before a redo runs.</summary>
    public Task<bool> ClaimForRedoAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, from: "undone", to: "redoing", ct);

    /// <summary>Release a stranded redo claim: CAS <c>redoing → undone</c> (redo failed).</summary>
    public Task<bool> ReleaseRedoClaimAsync(Guid id, CancellationToken ct = default) =>
        TransitionAsync(id, from: "redoing", to: "undone", ct);

    /// <summary>
    /// Finalize an undo: set <c>execution_state = 'undone'</c>, clear the undo token, and write the
    /// optional inverse trace log — all in one save. Mirrors upstream <c>markUndone</c>.
    /// </summary>
    public async Task<ActionLog?> MarkUndoneAsync(Guid id, ActionLog? traceLog = null, CancellationToken ct = default)
    {
        var log = await _db.Set<ActionLog>().FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);
        if (log is null) return null;
        log.ExecutionState = "undone";
        log.UndoToken = null;
        log.UpdatedAt = DateTime.UtcNow;
        if (traceLog is not null)
        {
            var now = DateTime.UtcNow;
            if (traceLog.CreatedAt == default) traceLog.CreatedAt = now;
            traceLog.UpdatedAt = now;
            _db.Set<ActionLog>().Add(traceLog);
        }
        await _db.SaveChangesAsync(ct);
        return log;
    }

    /// <summary>
    /// Finalize a redo: transition back to <c>done</c> and mint a fresh undo token so the record can be
    /// undone again (keeps undo/redo cycling — a simplification of upstream's new-log-per-redo path,
    /// see ADR 0007). Mirrors the observable "redone → undoable again" behavior.
    /// </summary>
    public async Task<ActionLog?> MarkRedoneAsync(Guid id, string freshUndoToken, CancellationToken ct = default)
    {
        var log = await _db.Set<ActionLog>().FirstOrDefaultAsync(l => l.Id == id && l.DeletedAt == null, ct);
        if (log is null) return null;
        log.ExecutionState = "done";
        log.UndoToken = freshUndoToken;
        log.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return log;
    }

    private async Task<bool> TransitionAsync(Guid id, string from, string to, CancellationToken ct)
    {
        if (IsRelational)
        {
            var affected = await _db.Set<ActionLog>()
                .Where(l => l.Id == id && l.ExecutionState == from && l.DeletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.ExecutionState, to)
                    .SetProperty(l => l.UpdatedAt, DateTime.UtcNow), ct);
            return affected == 1;
        }

        // In-memory fallback (single-threaded tests): load-check-set.
        var log = await _db.Set<ActionLog>()
            .FirstOrDefaultAsync(l => l.Id == id && l.ExecutionState == from && l.DeletedAt == null, ct);
        if (log is null) return false;
        log.ExecutionState = to;
        log.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
