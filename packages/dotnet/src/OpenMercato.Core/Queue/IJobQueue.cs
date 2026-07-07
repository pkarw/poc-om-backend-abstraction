namespace OpenMercato.Core.Queue;

/// <summary>
/// Queue abstraction, equivalent of upstream packages/queue. Jobs are enqueued
/// by name onto a named queue with a JSON payload.
///
/// BullMQ compatibility status: this scaffold ships its own Redis data layout
/// (om:queue:* keys) — jobs are NOT yet interchangeable with Node BullMQ.
/// A BullMQ wire-protocol adapter behind this same interface is a tracked
/// porting task; see docs/decisions/0004-queue-bullmq-compatibility.md.
/// </summary>
public interface IJobQueue
{
    /// <summary>Enqueue a job and return its id.</summary>
    Task<string> EnqueueAsync(string queue, string jobName, object payload, CancellationToken ct = default);
}

/// <summary>
/// A queue processor contributed by a module (upstream workers/*.ts).
/// Registered via IModule.ConfigureServices as a singleton IJobHandler.
/// </summary>
public interface IJobHandler
{
    /// <summary>Name of the queue this handler consumes (e.g. "health_check").</summary>
    string Queue { get; }

    Task HandleAsync(JobContext job, CancellationToken ct);
}

/// <summary>Job data handed to an IJobHandler.</summary>
public sealed record JobContext(string Id, string Name, string Queue, string PayloadJson);

/// <summary>Redis key layout used by the scaffold queue implementation.</summary>
public static class QueueKeys
{
    public static string IdCounter(string queue) => $"om:queue:{queue}:id";
    public static string Wait(string queue) => $"om:queue:{queue}:wait";
    public static string Active(string queue) => $"om:queue:{queue}:active";
    public static string Failed(string queue) => $"om:queue:{queue}:failed";
    public static string Job(string queue, string id) => $"om:queue:{queue}:jobs:{id}";
}
