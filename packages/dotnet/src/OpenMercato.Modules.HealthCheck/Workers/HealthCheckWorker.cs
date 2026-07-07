using Microsoft.Extensions.Logging;
using OpenMercato.Core.Queue;

namespace OpenMercato.Modules.HealthCheck.Workers;

/// <summary>
/// No-op reference worker on queue "health_check" (upstream equivalent: workers/*.ts).
/// Jobs are enqueued by POST /api/health_check/ping.
/// </summary>
public sealed class HealthCheckWorker : IJobHandler
{
    private readonly ILogger<HealthCheckWorker> _logger;

    public HealthCheckWorker(ILogger<HealthCheckWorker> logger)
    {
        _logger = logger;
    }

    public string Queue => "health_check";

    public Task HandleAsync(JobContext job, CancellationToken ct)
    {
        _logger.LogInformation(
            "health_check job {JobId} ({JobName}) processed (no-op). Payload: {Payload}",
            job.Id, job.Name, job.PayloadJson);
        return Task.CompletedTask;
    }
}
