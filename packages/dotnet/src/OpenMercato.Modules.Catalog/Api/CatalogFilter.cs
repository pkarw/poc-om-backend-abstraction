using OpenMercato.Core.Crud;

namespace OpenMercato.Modules.Catalog.Api;

/// <summary>Small shared helpers for the catalog CRUD route groups: boolean-flag parsing (upstream
/// <c>parseBooleanFlag</c>) and the create/delete id resolution from body or query.</summary>
public static class CatalogFilter
{
    public static bool TryBool(string? raw, out bool value)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": value = true; return true;
            case "0": case "false": case "no": value = false; return true;
            default: value = false; return false;
        }
    }

    public static Guid? ResolveDeleteId(CrudMutationContext m)
    {
        if (CatalogHttp.GuidOf(m.Body, "id") is { } fromBody) return fromBody;
        if (m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g)) return g;
        return null;
    }
}
