using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;

namespace OpenMercato.Core.Commands;

/// <summary>
/// Dispatches named write commands and owns the write pipeline — the port of upstream
/// <c>CommandBus</c> (packages/shared/src/lib/commands/command-bus.ts). Registered request-scoped.
///
/// <b>Execute:</b> resolve the <see cref="ICommand"/> registered for the id → run
/// <c>ExecuteAsync</c> inside a DB transaction (relational providers; the <c>withAtomicFlush</c>
/// equivalent) → build log metadata (<see cref="ICommandLogMetadataBuilder{TInput,TResult}"/>) → mint
/// an undo token for undoable commands → persist an <c>action_logs</c> row whose
/// <c>command_payload</c> wraps the input in a <c>{ "__redoInput": … }</c> redo envelope → return the
/// result (and, via <see cref="ExecuteWithLog{TInput,TResult}"/>, the log row for the
/// <c>x-om-operation</c> header).
///
/// <b>Undo:</b> load the row by token → CAS <c>done → undoing</c> → dispatch
/// <see cref="IUndoableCommand.UndoAsync"/> → mark <c>undone</c> (+ inverse trace log); on failure the
/// claim is released back to <c>done</c>. <b>Redo:</b> load the undone row by id → CAS
/// <c>undone → redoing</c> → dispatch <see cref="IUndoableCommand.RedoAsync"/> → mark <c>done</c> with
/// a fresh token; on failure the claim is released back to <c>undone</c>.
///
/// PARITY-TODO (clean extension points, deferred): before/after command interceptors, CRUD cache
/// invalidation, and the deferred side-effect flush (events + query-index) upstream runs after commit.
/// </summary>
public sealed class CommandBus
{
    private readonly AppDbContext _db;
    private readonly ActionLogService _actionLogs;
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, ICommand> _handlers;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CommandBus(AppDbContext db, ActionLogService actionLogs, IServiceProvider services, IEnumerable<ICommand> commands)
    {
        _db = db;
        _actionLogs = actionLogs;
        _services = services;
        var map = new Dictionary<string, ICommand>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (string.IsNullOrEmpty(command.CommandId))
                throw new InvalidOperationException($"Command handler {command.GetType().FullName} must define a CommandId.");
            // Last registration wins (parity with dev re-registration); DI order is registration order.
            map[command.CommandId] = command;
        }
        _handlers = map;
    }

    private bool IsRelational =>
        _db.Database.ProviderName is not null &&
        _db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>Execute a command and return only its result (upstream <c>execute(...).result</c>).</summary>
    public async Task<TResult> Execute<TInput, TResult>(string commandId, TInput input, CommandContext ctx, CancellationToken ct = default)
        => (await ExecuteWithLog<TInput, TResult>(commandId, input, ctx, ct)).Result;

    /// <summary>Execute a command and return the result plus the persisted action-log row (or null).</summary>
    public async Task<CommandExecuteResult<TResult>> ExecuteWithLog<TInput, TResult>(
        string commandId, TInput input, CommandContext ctx, CancellationToken ct = default)
    {
        var handler = ResolveHandler(commandId);
        if (handler is not ICommand<TInput, TResult> typed)
            throw new InvalidOperationException(
                $"Command '{commandId}' ({handler.GetType().FullName}) does not implement ICommand<{typeof(TInput).Name},{typeof(TResult).Name}>.");

        // Run ExecuteAsync inside a transaction (relational) — the withAtomicFlush equivalent.
        TResult result;
        if (IsRelational)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            result = await typed.ExecuteAsync(input, ctx, _services);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        else
        {
            result = await typed.ExecuteAsync(input, ctx, _services);
            await _db.SaveChangesAsync(ct);
        }

        // Build log metadata (optional contributor) and persist the action-log row.
        CommandLogMetadata? meta = handler is ICommandLogMetadataBuilder<TInput, TResult> builder
            ? builder.BuildLog(input, result, ctx)
            : null;

        var undoable = handler is IUndoableCommand;
        var log = await PersistLog(commandId, input, ctx, meta, undoable, ct);
        return new CommandExecuteResult<TResult>(result, log);
    }

    /// <summary>
    /// Undo a previously executed command by its undo token. Atomically claims the row
    /// (<c>done → undoing</c>), dispatches <see cref="IUndoableCommand.UndoAsync"/>, then marks it
    /// <c>undone</c> and writes an inverse trace log. Throws when the token is unknown/consumed or the
    /// command is not undoable.
    /// </summary>
    public async Task Undo(string undoToken, CommandContext ctx, CancellationToken ct = default)
    {
        var log = await _actionLogs.FindByUndoTokenAsync(undoToken, ct)
                  ?? throw new InvalidOperationException("Undo token expired or not found");
        var handler = ResolveHandler(log.CommandId);
        if (handler is not IUndoableCommand undoable)
            throw new InvalidOperationException($"Command {log.CommandId} is not undoable");

        var claimed = await _actionLogs.ClaimForUndoAsync(log.Id, ct);
        if (!claimed) throw new InvalidOperationException("Undo token already consumed");

        try
        {
            await undoable.UndoAsync(log, ctx, _services);
            await _actionLogs.MarkUndoneAsync(log.Id, BuildUndoTraceLog(log, ctx), ct);
        }
        catch
        {
            // Release the claim so the action remains retryable instead of being stranded in 'undoing'.
            try { await _actionLogs.ReleaseUndoClaimAsync(log.Id, ct); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Redo a previously undone command by its action-log id. Atomically claims the row
    /// (<c>undone → redoing</c>), dispatches <see cref="IUndoableCommand.RedoAsync"/> (which
    /// re-materializes the record reusing its id), then marks it <c>done</c> with a fresh undo token so
    /// it can be undone again. Throws when the row is missing or not in the <c>undone</c> state.
    /// </summary>
    public async Task Redo(Guid actionLogId, CommandContext ctx, CancellationToken ct = default)
    {
        var log = await _actionLogs.FindByIdAsync(actionLogId, ct)
                  ?? throw new InvalidOperationException("Action log not found");
        if (log.ExecutionState != "undone")
            throw new InvalidOperationException($"Action {actionLogId} is not in an undone state (was '{log.ExecutionState}')");
        var handler = ResolveHandler(log.CommandId);
        if (handler is not IUndoableCommand undoable)
            throw new InvalidOperationException($"Command {log.CommandId} is not undoable");

        var claimed = await _actionLogs.ClaimForRedoAsync(log.Id, ct);
        if (!claimed) throw new InvalidOperationException("Action already being redone");

        try
        {
            await undoable.RedoAsync(log, ctx, _services);
            await _actionLogs.MarkRedoneAsync(log.Id, Guid.NewGuid().ToString(), ct);
        }
        catch
        {
            try { await _actionLogs.ReleaseRedoClaimAsync(log.Id, ct); } catch { /* best-effort */ }
            throw;
        }
    }

    private ICommand ResolveHandler(string commandId)
    {
        if (_handlers.TryGetValue(commandId, out var handler)) return handler;
        var module = commandId.Split('.')[0];
        var sameModule = _handlers.Keys.Where(id => id.Split('.')[0] == module).ToList();
        var hint = sameModule.Count > 0
            ? $" Registered commands for module '{module}': [{string.Join(", ", sameModule)}]."
            : $" No commands registered for module '{module}'. Register it via services.AddScoped<ICommand, …>().";
        throw new InvalidOperationException($"Command handler not registered for id {commandId}.{hint}");
    }

    private async Task<ActionLog?> PersistLog<TInput>(
        string commandId, TInput input, CommandContext ctx, CommandLogMetadata? meta, bool undoable, CancellationToken ct)
    {
        if (meta?.SkipLog == true) return null;
        // Non-undoable commands with no contributed metadata write no row (parity: buildLog==null).
        if (!undoable && meta is null) return null;

        var log = new ActionLog
        {
            Id = Guid.NewGuid(),
            CommandId = commandId,
            TenantId = meta?.TenantId ?? ctx.TenantId,
            OrganizationId = meta?.OrganizationId ?? ctx.OrganizationId,
            ActorUserId = meta?.ActorUserId ?? ctx.UserId,
            ActionLabel = meta?.ActionLabel,
            ResourceKind = meta?.ResourceKind,
            ResourceId = meta?.ResourceId,
            ParentResourceKind = meta?.ParentResourceKind,
            ParentResourceId = meta?.ParentResourceId,
            RelatedResourceKind = meta?.RelatedResourceKind,
            RelatedResourceId = meta?.RelatedResourceId,
            ExecutionState = "done",
            UndoToken = undoable ? Guid.NewGuid().ToString() : null,
            CommandPayload = BuildRedoEnvelope(input),
            SnapshotBefore = Serialize(meta?.SnapshotBefore),
            SnapshotAfter = Serialize(meta?.SnapshotAfter),
            ChangesJson = Serialize(meta?.Changes),
            ContextJson = Serialize(meta?.Context),
        };

        // Auto-diff before/after snapshots into changes when the handler didn't supply an explicit diff
        // (upstream deriveActionLogChangedFields). Populates the changelog's field-level rows + the
        // ActionType/ChangedFields/PrimaryChangedField projection columns used by filters/export.
        if (log.ChangesJson is null && log.SnapshotBefore is not null && log.SnapshotAfter is not null)
        {
            var diff = ActionLogProjection.DiffSnapshots(log.SnapshotBefore, log.SnapshotAfter);
            if (diff.Count > 0)
            {
                log.ChangesJson = JsonSerializer.Serialize(diff, JsonOptions);
                log.ChangedFields = diff.Keys.ToArray();
                log.PrimaryChangedField = diff.Keys.First();
            }
        }
        log.ActionType ??= ActionLogProjection.DeriveActionType(commandId);

        return await _actionLogs.LogAsync(log, ct);
    }

    private ActionLog BuildUndoTraceLog(ActionLog log, CommandContext ctx)
    {
        var context = new JsonObject
        {
            ["historyAction"] = "undo",
            ["sourceLogId"] = log.Id.ToString(),
            ["sourceCommandId"] = log.CommandId,
        };
        return new ActionLog
        {
            Id = Guid.NewGuid(),
            CommandId = log.CommandId,
            TenantId = log.TenantId ?? ctx.TenantId,
            OrganizationId = log.OrganizationId ?? ctx.OrganizationId,
            ActorUserId = ctx.UserId ?? log.ActorUserId,
            ActionLabel = log.ActionLabel,
            ResourceKind = log.ResourceKind,
            ResourceId = log.ResourceId,
            ParentResourceKind = log.ParentResourceKind,
            ParentResourceId = log.ParentResourceId,
            RelatedResourceKind = log.RelatedResourceKind,
            RelatedResourceId = log.RelatedResourceId,
            ExecutionState = "done",
            UndoToken = null,
            // Inverse trace: swap before/after so the row reads as the reverse operation.
            SnapshotBefore = log.SnapshotAfter,
            SnapshotAfter = log.SnapshotBefore,
            ContextJson = context.ToJsonString(JsonOptions),
        };
    }

    private static string BuildRedoEnvelope<TInput>(TInput input)
    {
        var envelope = new JsonObject { ["__redoInput"] = JsonSerializer.SerializeToNode(input, JsonOptions) };
        return envelope.ToJsonString(JsonOptions);
    }

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);
}
