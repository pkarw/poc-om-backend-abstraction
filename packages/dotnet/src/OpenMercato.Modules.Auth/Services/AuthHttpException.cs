namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Carries an HTTP status + JSON body out of a guard/command helper, mirroring upstream
/// <c>CrudHttpError</c> (packages/shared/src/lib/crud/errors.ts). Route handlers catch this and
/// return <c>Results.Json(Body, statusCode: Status)</c> so guard failures reproduce the exact
/// upstream envelope (e.g. 403 <c>{"error":"..."}</c>, 404 <c>{"error":"Role not found"}</c>).
/// </summary>
public sealed class AuthHttpException : Exception
{
    public int Status { get; }
    public object Body { get; }

    public AuthHttpException(int status, object body)
        : base(body is { } b ? b.ToString() : "auth error")
    {
        Status = status;
        Body = body;
    }

    /// <summary>Upstream <c>forbidden(message)</c> — 403 <c>{"error":message}</c>.</summary>
    public static AuthHttpException Forbidden(string message) =>
        new(403, new { error = message });
}
