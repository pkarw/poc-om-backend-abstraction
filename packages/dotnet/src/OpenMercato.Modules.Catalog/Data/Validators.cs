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
    private static readonly System.Text.RegularExpressions.Regex Slug =
        new("^[a-z0-9_-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

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

    /// <summary>categoryCreateSchema: <c>name</c> is required (min 1, max 255).</summary>
    public static IReadOnlyList<CrudValidationIssue> Category(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var name = CatalogHttp.Str(body, "name")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255)
            issues.Add(new CrudValidationIssue(new[] { "name" }, "name is required", "invalid_string"));
        return issues;
    }

    /// <summary>variantCreateSchema: <c>productId</c> is required (uuid).</summary>
    public static IReadOnlyList<CrudValidationIssue> Variant(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (CatalogHttp.GuidOf(body, "productId") is null)
            issues.Add(new CrudValidationIssue(new[] { "productId" }, "productId is required", "invalid_uuid"));
        return issues;
    }

    /// <summary>offerCreateSchema: <c>productId</c>, <c>channelId</c> (uuid) and <c>title</c> are required.</summary>
    public static IReadOnlyList<CrudValidationIssue> Offer(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (CatalogHttp.GuidOf(body, "productId") is null)
            issues.Add(new CrudValidationIssue(new[] { "productId" }, "productId is required", "invalid_uuid"));
        if (CatalogHttp.GuidOf(body, "channelId") is null)
            issues.Add(new CrudValidationIssue(new[] { "channelId" }, "channelId is required", "invalid_uuid"));
        var title = CatalogHttp.Str(body, "title")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 255)
            issues.Add(new CrudValidationIssue(new[] { "title" }, "title is required", "invalid_string"));
        return issues;
    }

    /// <summary>priceKindCreateSchema: <c>code</c> (slug) and <c>title</c> are required.</summary>
    public static IReadOnlyList<CrudValidationIssue> PriceKind(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var code = CatalogHttp.Str(body, "code")?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 80 || !Slug.IsMatch(code))
            issues.Add(new CrudValidationIssue(new[] { "code" }, "code must match ^[a-z0-9_-]+$", "invalid_string"));
        var title = CatalogHttp.Str(body, "title")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 255)
            issues.Add(new CrudValidationIssue(new[] { "title" }, "title is required", "invalid_string"));
        return issues;
    }

    /// <summary>priceCreateSchema: <c>currencyCode</c> and <c>priceKindId</c> (uuid) are required.</summary>
    public static IReadOnlyList<CrudValidationIssue> Price(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        var currency = CatalogHttp.Str(body, "currencyCode")?.Trim();
        if (string.IsNullOrEmpty(currency))
            issues.Add(new CrudValidationIssue(new[] { "currencyCode" }, "currencyCode is required", "invalid_string"));
        if (CatalogHttp.GuidOf(body, "priceKindId") is null)
            issues.Add(new CrudValidationIssue(new[] { "priceKindId" }, "priceKindId is required", "invalid_uuid"));
        return issues;
    }
}
