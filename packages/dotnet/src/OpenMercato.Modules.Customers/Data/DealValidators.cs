using System.Text.Json;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Customers.Api;

namespace OpenMercato.Modules.Customers.Data;

/// <summary>
/// Create-time request validators for the deals CRUD write surface — the port of the create-required
/// slice of <c>dealCreateSchema</c> (data/validators.ts). Only <c>title</c> (1–200) is enforced at the
/// factory boundary (returning the shared <see cref="CrudValidationIssue"/> list → 400 <c>{error,details}</c>);
/// the command handler owns the rest (pipeline-stage resolution, link syncs, cf persistence).
/// </summary>
public static class DealValidators
{
    public static IReadOnlyList<CrudValidationIssue> Deal(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var title = CustomersHttp.Str(body, "title")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200)
            issues.Add(new CrudValidationIssue(new[] { "title" }, "title is required", "invalid_string"));
        return issues;
    }
}
