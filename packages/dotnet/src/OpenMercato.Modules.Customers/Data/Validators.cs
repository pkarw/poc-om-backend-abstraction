using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Customers.Api;

namespace OpenMercato.Modules.Customers.Data;

/// <summary>
/// Request validators for the customers CRUD writes — the port of the relevant <c>data/validators.ts</c>
/// schemas (personCreate/companyCreate/addressCreate/tagCreate). Return the shared
/// <see cref="CrudValidationIssue"/> list the factory maps to the 400 <c>{error,details}</c> body.
/// Only create-required fields are enforced here; the command handlers enforce the rest.
/// </summary>
public static class CustomersValidators
{
    private static readonly Regex TagSlug = new("^[a-z0-9_-]+$", RegexOptions.Compiled);

    public static IReadOnlyList<CrudValidationIssue> Person(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        RequireString(body, "firstName", 1, 120, issues);
        RequireString(body, "lastName", 1, 120, issues);
        return issues;
    }

    public static IReadOnlyList<CrudValidationIssue> Company(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        RequireString(body, "displayName", 1, 200, issues);
        return issues;
    }

    public static IReadOnlyList<CrudValidationIssue> Address(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (CustomersHttp.GuidOf(body, "entityId") is null)
            issues.Add(new CrudValidationIssue(new[] { "entityId" }, "entityId is required", "invalid_uuid"));
        RequireString(body, "addressLine1", 1, 300, issues);
        return issues;
    }

    public static IReadOnlyList<CrudValidationIssue> Tag(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var slug = CustomersHttp.Str(body, "slug")?.Trim();
        if (string.IsNullOrEmpty(slug) || slug.Length > 80 || !TagSlug.IsMatch(slug))
            issues.Add(new CrudValidationIssue(new[] { "slug" }, "slug must match ^[a-z0-9_-]+$", "invalid_string"));
        RequireString(body, "label", 1, 120, issues);
        return issues;
    }

    private static void RequireString(JsonElement body, string field, int min, int max, List<CrudValidationIssue> issues)
    {
        var v = CustomersHttp.Str(body, field)?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length < min || v.Length > max)
            issues.Add(new CrudValidationIssue(new[] { field }, $"{field} is required", "invalid_string"));
    }
}
