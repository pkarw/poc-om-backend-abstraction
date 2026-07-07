using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenMercato.Core.Events;

/// <summary>Publish side of the event system (upstream packages/events).</summary>
public interface IEventBus
{
    Task PublishAsync(string eventName, object payload, CancellationToken ct = default);
}

/// <summary>
/// An event subscriber contributed by a module (upstream subscribers/*.ts).
/// Registered via IModule.ConfigureServices as a singleton IEventSubscriber.
/// </summary>
public interface IEventSubscriber
{
    /// <summary>Event name to subscribe to, dot-notation like upstream (e.g. "health_check.pinged").</summary>
    string Event { get; }

    Task HandleAsync(string payloadJson, CancellationToken ct);
}

/// <summary>
/// In-process event bus (equivalent of upstream EVENTS_STRATEGY=local).
/// Dispatches synchronously to all matching subscribers; a subscriber failure
/// is logged and does not affect other subscribers or the publisher.
/// A Redis-backed distributed bus is a tracked porting task.
/// </summary>
public sealed class LocalEventBus : IEventBus
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<IEventSubscriber> _subscribers;
    private readonly ILogger<LocalEventBus> _logger;

    public LocalEventBus(IEnumerable<IEventSubscriber> subscribers, ILogger<LocalEventBus> logger)
    {
        _subscribers = subscribers.ToList();
        _logger = logger;
    }

    public async Task PublishAsync(string eventName, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        foreach (var subscriber in _subscribers.Where(s => s.Event == eventName))
        {
            try
            {
                await subscriber.HandleAsync(json, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscriber {Subscriber} failed for event '{Event}'.",
                    subscriber.GetType().Name, eventName);
            }
        }
    }
}
