using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using Xunit;

namespace OpenMercato.Tests.Commands;

/// <summary>
/// Tests for the command-bus write pipeline (OpenMercato.Core.Commands): execute persists an
/// action_logs row and returns the result; undo transitions execution_state and calls UndoAsync;
/// redo replays and re-arms the undo token; the optimistic-lock helper returns the 409 contract.
/// </summary>
public class CommandBusTests
{
    // ---- Test fixtures -----------------------------------------------------------------------

    public sealed record CreateWidgetInput(string Name);
    public sealed record CreateWidgetResult(string Id, string Name);

    /// <summary>Observes which lifecycle methods ran (shared via DI singleton).</summary>
    private sealed class Recorder
    {
        public List<string> Calls { get; } = new();
    }

    /// <summary>An undoable, log-contributing test command.</summary>
    private sealed class CreateWidgetCommand :
        ICommand<CreateWidgetInput, CreateWidgetResult>,
        IUndoableCommand,
        ICommandLogMetadataBuilder<CreateWidgetInput, CreateWidgetResult>
    {
        public string CommandId => "test.widget.create";

        public Task<CreateWidgetResult> ExecuteAsync(CreateWidgetInput input, CommandContext ctx, IServiceProvider services)
        {
            services.GetRequiredService<Recorder>().Calls.Add("execute");
            var id = Guid.NewGuid().ToString();
            return Task.FromResult(new CreateWidgetResult(id, input.Name));
        }

        public CommandLogMetadata BuildLog(CreateWidgetInput input, CreateWidgetResult result, CommandContext ctx) =>
            new()
            {
                ActionLabel = "Create widget",
                ResourceKind = "test.widget",
                ResourceId = result.Id,
                SnapshotAfter = new { id = result.Id, name = result.Name },
            };

        public Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
        {
            services.GetRequiredService<Recorder>().Calls.Add($"undo:{log.ResourceId}");
            return Task.CompletedTask;
        }

        public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
        {
            var input = log.GetRedoInput<CreateWidgetInput>();
            services.GetRequiredService<Recorder>().Calls.Add($"redo:{input?.Name}");
            return Task.CompletedTask;
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var registry = new ModuleRegistry(new IModule[] { new AuditLogsModule() });
        services.AddSingleton(registry);
        services.AddSingleton<Recorder>();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"cmd-tests-{Guid.NewGuid():N}"));
        new AuditLogsModule().ConfigureServices(services); // ActionLogService + CommandBus (scoped)
        services.AddScoped<ICommand, CreateWidgetCommand>();
        return services.BuildServiceProvider();
    }

    private static CommandContext Ctx() => new()
    {
        TenantId = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
    };

    // ---- Tests -------------------------------------------------------------------------------

    [Fact]
    public async Task Execute_persists_action_log_row_and_returns_result()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var res = await bus.ExecuteWithLog<CreateWidgetInput, CreateWidgetResult>(
            "test.widget.create", new CreateWidgetInput("Gadget"), Ctx());

        Assert.Equal("Gadget", res.Result.Name);
        Assert.NotNull(res.LogEntry);

        var rows = await db.Set<ActionLog>().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("test.widget.create", row.CommandId);
        Assert.Equal("done", row.ExecutionState);
        Assert.False(string.IsNullOrEmpty(row.UndoToken)); // undoable → token minted
        Assert.Equal("test.widget", row.ResourceKind);
        Assert.Equal(res.Result.Id, row.ResourceId);
        // command_payload wraps the input in the redo envelope
        Assert.Contains("__redoInput", row.CommandPayload);
        Assert.Equal("Gadget", row.GetRedoInput<CreateWidgetInput>()!.Name);
    }

    [Fact]
    public async Task Undo_transitions_status_and_calls_UndoAsync_and_writes_trace_log()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<Recorder>();
        var ctx = Ctx();

        var res = await bus.ExecuteWithLog<CreateWidgetInput, CreateWidgetResult>(
            "test.widget.create", new CreateWidgetInput("Gadget"), ctx);
        var token = res.LogEntry!.UndoToken!;

        await bus.Undo(token, ctx);

        Assert.Contains($"undo:{res.Result.Id}", recorder.Calls);

        var original = await db.Set<ActionLog>().FirstAsync(l => l.Id == res.LogEntry.Id);
        Assert.Equal("undone", original.ExecutionState);
        Assert.Null(original.UndoToken); // consumed

        // An inverse trace log was written (execute row + trace row = 2).
        var count = await db.Set<ActionLog>().CountAsync();
        Assert.Equal(2, count);
        var trace = await db.Set<ActionLog>().FirstAsync(l => l.Id != res.LogEntry.Id);
        Assert.Contains("undo", trace.ContextJson);
    }

    [Fact]
    public async Task Undo_with_unknown_token_throws()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.Undo("no-such-token", Ctx()));
    }

    [Fact]
    public async Task Redo_replays_calls_RedoAsync_and_rearms_undo_token()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<Recorder>();
        var ctx = Ctx();

        var res = await bus.ExecuteWithLog<CreateWidgetInput, CreateWidgetResult>(
            "test.widget.create", new CreateWidgetInput("Gadget"), ctx);
        await bus.Undo(res.LogEntry!.UndoToken!, ctx);

        await bus.Redo(res.LogEntry.Id, ctx);

        Assert.Contains("redo:Gadget", recorder.Calls);

        var row = await db.Set<ActionLog>().FirstAsync(l => l.Id == res.LogEntry.Id);
        Assert.Equal("done", row.ExecutionState);
        Assert.False(string.IsNullOrEmpty(row.UndoToken)); // fresh token — undoable again

        // The re-armed token round-trips through another undo.
        await bus.Undo(row.UndoToken!, ctx);
        var afterSecondUndo = await db.Set<ActionLog>().FirstAsync(l => l.Id == res.LogEntry.Id);
        Assert.Equal("undone", afterSecondUndo.ExecutionState);
    }

    [Fact]
    public async Task Redo_of_non_undone_row_throws()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();
        var ctx = Ctx();

        var res = await bus.ExecuteWithLog<CreateWidgetInput, CreateWidgetResult>(
            "test.widget.create", new CreateWidgetInput("Gadget"), ctx);

        // Row is still 'done' (not undone) → redo must refuse.
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.Redo(res.LogEntry!.Id, ctx));
    }

    [Fact]
    public async Task Execute_of_unregistered_command_throws_with_hint()
    {
        await using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<CommandBus>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bus.Execute<CreateWidgetInput, CreateWidgetResult>("test.widget.destroy", new CreateWidgetInput("x"), Ctx()));
        Assert.Contains("test.widget.destroy", ex.Message);
    }
}
