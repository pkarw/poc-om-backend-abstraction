namespace OpenMercato.Core.Modules;

/// <summary>
/// The runtime "supported" surface for notification types: what the platform
/// knows a module may emit. Built from <see cref="ModuleRegistry.AllNotificationTypes"/>.
///
/// PORT-TODO: the actual delivery/storage engine (recipient fan-out, persistence,
/// read/unread state) arrives with the notifications module port. This catalog is
/// only the declared-type lookup upstream's <c>buildNotificationFromType</c> needs.
/// </summary>
public interface INotificationCatalog
{
    /// <summary>True when <paramref name="type"/> was declared by some module.</summary>
    bool IsKnown(string type);

    /// <summary>The declaration for <paramref name="type"/>, or null if unknown.</summary>
    NotificationTypeDefinition? Get(string type);

    /// <summary>All declared notification types.</summary>
    IReadOnlyList<NotificationTypeDefinition> All { get; }
}

/// <summary>Default in-memory notification catalog built from the module registry.</summary>
public sealed class NotificationCatalog : INotificationCatalog
{
    private readonly Dictionary<string, NotificationTypeDefinition> _byType;

    public NotificationCatalog(ModuleRegistry registry)
    {
        _byType = registry.AllNotificationTypes.ToDictionary(n => n.Type, StringComparer.Ordinal);
    }

    public bool IsKnown(string type) => _byType.ContainsKey(type);

    public NotificationTypeDefinition? Get(string type) =>
        _byType.TryGetValue(type, out var def) ? def : null;

    public IReadOnlyList<NotificationTypeDefinition> All => _byType.Values.ToList();
}
