using System.Text.Json;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Catalog.Api;

namespace OpenMercato.Modules.Catalog.Data;

/// <summary>
/// Request validators for the catalog CRUD writes — the port of the relevant <c>data/validators.ts</c>
/// schemas. Return the shared <see cref="CrudValidationIssue"/> list the factory maps to the 400
/// <c>{error,details}</c> body. Only create-required fields are enforced here (matching the customers
/// port's discipline); the command handlers enforce the rest.
/// </summary>
public static class CatalogValidators
{
    /// <summary>productCreateSchema: <c>title</c> is the only unconditionally-required field
    /// (min 1, max 255). productType has a server default ('simple').</summary>
    public static IReadOnlyList<CrudValidationIssue> Product(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var title = CatalogHttp.Str(body, "title")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 255)
            issues.Add(new CrudValidationIssue(new[] { "title" }, "title is required", "invalid_string"));
        return issues;
    }
}
