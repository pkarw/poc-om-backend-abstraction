namespace OpenMercato.Core.Commands;

/// <summary>
/// A typed HTTP error carrying an exact status code + JSON body — the port of upstream
/// <c>CrudHttpError</c> (packages/shared/src/lib/crud/errors.ts). Thrown by command handlers, scope
/// guards (<see cref="CommandScope"/>) and the optimistic-lock helper (<see cref="OptimisticLock"/>);
/// the CRUD factory / route layer maps it straight through to the response (status + <see cref="Body"/>).
/// </summary>
public class CommandHttpException : Exception
{
    public int Status { get; }

    /// <summary>The JSON response body (e.g. <c>{ error = "Forbidden" }</c>).</summary>
    public object Body { get; }

    public CommandHttpException(int status, object body)
        : base(ExtractMessage(body))
    {
        Status = status;
        Body = body;
    }

    private static string ExtractMessage(object body)
    {
        if (body is string s) return s;
        var prop = body.GetType().GetProperty("error");
        var val = prop?.GetValue(body) as string;
        return val ?? "Request failed";
    }

    public static CommandHttpException Forbidden(string message = "Forbidden") =>
        new(403, new { error = message });

    public static CommandHttpException NotFound(string message = "Not found") =>
        new(404, new { error = message });

    public static CommandHttpException Conflict(string message) =>
        new(409, new { error = message });

    public static CommandHttpException BadRequest(string message) =>
        new(400, new { error = message });
}
