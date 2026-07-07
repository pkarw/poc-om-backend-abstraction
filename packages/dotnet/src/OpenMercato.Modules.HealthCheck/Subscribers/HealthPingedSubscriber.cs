using Microsoft.Extensions.Logging;
using OpenMercato.Core.Events;

namespace OpenMercato.Modules.HealthCheck.Subscribers;

/// <summary>
/// Reference event subscriber (upstream equivalent: subscribers/*.ts).
/// Listens for "health_check.pinged" published by POST /api/health_check/ping.
/// </summary>
public sealed class HealthPingedSubscriber : IEventSubscriber
{
    private readonly ILogger<HealthPingedSubscriber> _logger;

    public HealthPingedSubscriber(ILogger<HealthPingedSubscriber> logger)
    {
        _logger = logger;
    }

    public string Event => "health_check.pinged";

    public Task HandleAsync(string payloadJson, CancellationToken ct)
    {
        _logger.LogInformation("Event health_check.pinged received: {Payload}", payloadJson);
        return Task.CompletedTask;
    }
}
