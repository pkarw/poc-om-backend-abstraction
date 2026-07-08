namespace OpenMercato.Core.Commands;

/// <summary>
/// Non-generic marker for a command handler so the <see cref="CommandBus"/> can resolve all
/// registered handlers from DI (<c>IEnumerable&lt;ICommand&gt;</c>) and index them by
/// <see cref="CommandId"/>. Every handler also implements <see cref="ICommand{TInput,TResult}"/>.
/// </summary>
public interface ICommand
{
    /// <summary>Stable id, <c>'&lt;module&gt;.&lt;domain&gt;.&lt;action&gt;'</c> (e.g. <c>customers.people.create</c>).
    /// Mirrors upstream <c>CommandHandler.id</c> / <c>registerCommand</c>.</summary>
    string CommandId { get; }
}

/// <summary>
/// A write command — the port of upstream <c>CommandHandler.execute</c>
/// (packages/shared/src/lib/commands/types.ts). Handlers do their DB work through the request-scoped
/// <c>AppDbContext</c> resolved from <paramref name="services"/>; the <see cref="CommandBus"/> wraps
/// the call in a transaction (relational providers) and persists the action-log row afterwards.
///
/// Modules contribute a command by registering it in DI:
/// <code>services.AddScoped&lt;ICommand, CreatePersonCommand&gt;();</code>
/// (mirrors upstream's side-effect <c>registerCommand(handler)</c> in a module's index.ts).
/// </summary>
public interface ICommand<in TInput, TResult> : ICommand
{
    Task<TResult> ExecuteAsync(TInput input, CommandContext ctx, IServiceProvider services);
}

/// <summary>
/// Optional undo/redo capability for a command — the port of upstream <c>CommandHandler.undo</c> /
/// <c>CommandHandler.redo</c>. When a handler implements this, the <see cref="CommandBus"/> mints an
/// undo token on execute and dispatches <see cref="UndoAsync"/>/<see cref="RedoAsync"/> to reverse or
/// replay the write, transitioning the action-log <c>execution_state</c> atomically.
///
/// Read the original input via <see cref="ActionLog.GetRedoInput{T}"/> and the row state via
/// <see cref="ActionLog.GetSnapshotBefore{T}"/>/<see cref="ActionLog.GetSnapshotAfter{T}"/>. Redo MUST
/// re-materialize the record reusing its original id (upstream invariant I6 / issue #2506).
/// </summary>
public interface IUndoableCommand
{
    /// <summary>Reverse the write recorded by <paramref name="log"/> (e.g. soft-delete a created row).</summary>
    Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services);

    /// <summary>Replay a previously undone write from <paramref name="log"/>, reusing the original id.</summary>
    Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services);
}

/// <summary>
/// Optional log-metadata contributor — the port of upstream <c>CommandHandler.buildLog</c> /
/// <c>captureAfter</c>. When a handler implements this, the <see cref="CommandBus"/> calls
/// <see cref="BuildLog"/> after <c>ExecuteAsync</c> and folds the returned metadata (resource
/// kind/id, snapshots, changes, action label, context) into the persisted action-log row. Handlers
/// that don't implement it still get a minimal row when they are undoable.
/// </summary>
public interface ICommandLogMetadataBuilder<in TInput, in TResult>
{
    CommandLogMetadata? BuildLog(TInput input, TResult result, CommandContext ctx);
}
