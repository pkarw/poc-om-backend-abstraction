using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace OpenMercato.Core.Queue;

/// <summary>
/// Hosted service that polls Redis and dispatches jobs to the IJobHandler
/// implementations registered by modules. One polling loop per distinct queue.
/// StackExchange.Redis multiplexes a single connection and cannot use blocking
/// commands (BRPOPLPUSH), so an idle queue is polled every 500 ms.
/// </summary>
public sealed class QueueWorkerService : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);

    private readonly IConnectionMultiplexer _redis;
    private readonly IReadOnlyList<IJobHandler> _handlers;
    private readonly ILogger<QueueWorkerService> _logger;

    public QueueWorkerService(
        IConnectionMultiplexer redis,
        IEnumerable<IJobHandler> handlers,
        ILogger<QueueWorkerService> logger)
    {
        _redis = redis;
        _handlers = handlers.ToList();
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var byQueue = _handlers
            .GroupBy(h => h.Queue)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IJobHandler>)g.ToList());

        if (byQueue.Count == 0)
        {
            _logger.LogWarning("Queue worker started but no IJobHandler is registered; idling.");
            return;
        }

        _logger.LogInformation("Queue worker started. Consuming queues: {Queues}",
            string.Join(", ", byQueue.Keys.OrderBy(k => k)));

        try
        {
            await Task.WhenAll(byQueue.Select(kv => PollQueueAsync(kv.Key, kv.Value, stoppingToken)));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task PollQueueAsync(string queue, IReadOnlyList<IJobHandler> handlers, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        var wait = QueueKeys.Wait(queue);
        var active = QueueKeys.Active(queue);

        while (!ct.IsCancellationRequested)
        {
            RedisValue jobId;
            try
            {
                jobId = await db.ListRightPopLeftPushAsync(wait, active);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis unavailable while polling queue '{Queue}'; retrying.", queue);
                await Task.Delay(ErrorDelay, ct);
                continue;
            }

            if (jobId.IsNullOrEmpty)
            {
                await Task.Delay(IdleDelay, ct);
                continue;
            }

            var id = jobId.ToString();
            var jobKey = QueueKeys.Job(queue, id);
            var fields = await db.HashGetAllAsync(jobKey);
            var map = fields.ToDictionary(f => f.Name.ToString(), f => f.Value.ToString());
            var job = new JobContext(
                Id: id,
                Name: map.GetValueOrDefault("name", string.Empty),
                Queue: queue,
                PayloadJson: map.GetValueOrDefault("data", "{}"));

            var failed = false;
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failed = true;
                    _logger.LogError(ex, "Job {JobId} ({JobName}) on queue '{Queue}' failed in {Handler}.",
                        job.Id, job.Name, queue, handler.GetType().Name);
                }
            }

            await db.ListRemoveAsync(active, jobId);
            if (failed)
            {
                await db.ListLeftPushAsync(QueueKeys.Failed(queue), jobId);
                await db.HashSetAsync(jobKey, "failedOn", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            else
            {
                await db.HashSetAsync(jobKey, "finishedOn", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
    }
}
