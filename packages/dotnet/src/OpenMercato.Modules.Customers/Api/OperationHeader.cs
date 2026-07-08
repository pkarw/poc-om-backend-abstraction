using System.Text.Json;
using OpenMercato.Core.Commands;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Builds the <c>x-om-operation</c> header for hand-written command-bus routes — the same
/// <c>omop:</c> + url-encoded JSON envelope the CRUD factory emits (serializeOperationMetadata). Only
/// present when the action-log row carries an undo token (an undoable operation).
/// </summary>
internal static class OperationHeader
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string? Build(ActionLog? log)
    {
        if (log is null || string.IsNullOrEmpty(log.UndoToken) || string.IsNullOrEmpty(log.CommandId)) return null;
        var payload = new
        {
            id = log.Id.ToString(),
            undoToken = log.UndoToken,
            commandId = log.CommandId,
            actionLabel = log.ActionLabel,
            resourceKind = log.ResourceKind,
            resourceId = log.ResourceId,
            executedAt = (log.CreatedAt == default
                ? DateTime.UtcNow
                : DateTime.SpecifyKind(log.CreatedAt, DateTimeKind.Utc)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        return "omop:" + Uri.EscapeDataString(JsonSerializer.Serialize(payload, Web));
    }
}
