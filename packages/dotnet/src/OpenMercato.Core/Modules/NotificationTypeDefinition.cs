namespace OpenMercato.Core.Modules;

/// <summary>
/// A notification type a module declares up front, mirroring upstream
/// <c>notifications.ts</c> (<c>notificationTypes: NotificationTypeDefinition[]</c>,
/// consumed by the notifications module's <c>buildNotificationFromType</c>).
///
/// Declaring the type is the "supported surface": it tells the platform which
/// notifications a module may create, their severity and retention. The actual
/// delivery/storage engine ships with the notifications module port later
/// (PORT-TODO), so at this stage the declaration only feeds
/// <see cref="INotificationCatalog"/>.
/// </summary>
/// <param name="Type">
/// Stable dotted id, e.g. <c>"auth.password_reset.requested"</c>. Unique across all modules.
/// </param>
/// <param name="Severity">One of <c>"info"</c>, <c>"success"</c>, <c>"warning"</c>, <c>"error"</c>.</param>
/// <param name="ExpiresAfterHours">Optional retention window in hours (null = never auto-expires).</param>
/// <param name="TitleTemplate">Optional title template (upstream template hook); may be null.</param>
/// <param name="BodyTemplate">Optional body template (upstream template hook); may be null.</param>
public record NotificationTypeDefinition(
    string Type,
    string Severity,
    int? ExpiresAfterHours = null,
    string? TitleTemplate = null,
    string? BodyTemplate = null);
