using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Shared dictionary/settings helpers — the port of upstream <c>api/dictionaries/context.ts</c>
/// (KIND_MAP + <c>resolveDictionaryRouteContext</c>) plus <c>commands/settings.ts</c>'s
/// <c>loadCustomerSettings</c> and <c>lib/roleTypeUsage.ts</c>. Pure/static; the routes call these
/// after resolving the request <see cref="CommandContext"/>.
///
/// NOTE — the dictionary list cache (prefix <c>customers:dictionaries</c>, TTL 300000ms) and the
/// mutation-guard hooks are intentionally omitted in this port: the .NET package ships no cache
/// strategy or mutation-guard runtime yet. See ADR — dictionary caching / mutation guard (Phase 2).
/// </summary>
internal static class DictionaryContext
{
    /// <summary>Builtin route kinds (order matches <c>BUILTIN_DICTIONARY_ROUTE_KINDS</c>).</summary>
    public static readonly string[] BuiltinRouteKinds =
    {
        "statuses", "sources", "lifecycle-stages", "address-types", "activity-types", "deal-statuses",
        "pipeline-stages", "job-titles", "industries", "temperature", "renewal-quarters", "person-company-roles",
    };

    private static readonly IReadOnlyDictionary<string, string> KindMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["statuses"] = "status",
        ["sources"] = "source",
        ["lifecycle-stages"] = "lifecycle_stage",
        ["address-types"] = "address_type",
        ["activity-types"] = "activity_type",
        ["deal-statuses"] = "deal_status",
        ["pipeline-stages"] = "pipeline_stage",
        ["job-titles"] = "job_title",
        ["industries"] = "industry",
        ["temperature"] = "temperature",
        ["renewal-quarters"] = "renewal_quarter",
        ["person-company-roles"] = "person_company_role",
    };

    private static readonly Regex CustomKind = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex HexColor = new("^#([0-9A-Fa-f]{6})$", RegexOptions.Compiled);

    /// <summary>Validate the <c>{kind}</c> route param and map to the stored kind (upstream
    /// <c>mapDictionaryKind</c>). Throws <see cref="FormatException"/> on an invalid kind (the caller's
    /// generic catch turns it into the route-specific 400, exactly as the Zod parse does upstream).</summary>
    public static (string RouteKind, string MappedKind) MapKind(string? kind)
    {
        var value = (kind ?? string.Empty).Trim();
        if (value.Length == 0 || !CustomKind.IsMatch(value))
            throw new FormatException("Invalid dictionary kind");
        return (value, KindMap.TryGetValue(value, out var mapped) ? mapped : value);
    }

    /// <summary>Load the per-org <c>CustomerSettings</c> row (upstream <c>loadCustomerSettings</c>).</summary>
    public static Task<CustomerSettings?> LoadSettingsAsync(AppDbContext db, Guid tenantId, Guid organizationId, CancellationToken ct = default) =>
        db.Set<CustomerSettings>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OrganizationId == organizationId, ct);

    /// <summary>Parse the <c>dictionary_sort_modes</c> jsonb into a validated {routeKind → sortMode} map
    /// (invalid keys/modes dropped — upstream <c>normalizeDictionarySortModes</c>).</summary>
    public static Dictionary<string, string> ParseSortModes(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var key = prop.Name.Trim();
                if (!CustomKind.IsMatch(key)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var mode = prop.Value.GetString();
                if (mode is not null && OpenMercato.Modules.Dictionaries.Lib.DictionaryEntrySortModes.All.Contains(mode))
                    result[key] = mode;
            }
        }
        catch { /* malformed jsonb → empty map */ }
        return result;
    }

    /// <summary>Serialize a sort-mode map back to jsonb.</summary>
    public static string SerializeSortModes(IReadOnlyDictionary<string, string> modes) =>
        JsonSerializer.Serialize(modes, CustomersHttp.Web);

    /// <summary>Resolve the readable organization scope (selected org + ancestors) for inheritance —
    /// the port of the ancestor walk in <c>resolveDictionaryRouteContext</c>. Returns the selected org
    /// first (highest priority), then ancestors.</summary>
    public static async Task<List<Guid>> ReadableOrganizationIdsAsync(
        AppDbContext db, Guid tenantId, CommandContext ctx, CancellationToken ct = default)
    {
        var candidates = new List<Guid>();
        void Push(Guid? id) { if (id is { } g && g != Guid.Empty && !candidates.Contains(g)) candidates.Add(g); }
        Push(ctx.OrganizationId);
        if (ctx.OrganizationIds is { } filter) foreach (var id in filter) Push(id);
        if (ctx.AllowedOrganizationIds is { } allowed) foreach (var id in allowed) Push(id);

        var readable = new List<Guid>();
        void Add(Guid id) { if (id != Guid.Empty && !readable.Contains(id)) readable.Add(id); }

        if (candidates.Count == 0) return readable;

        var orgs = await db.Set<Organization>().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.DeletedAt == null && candidates.Contains(o.Id))
            .Select(o => new { o.Id, o.AncestorIdsJson })
            .ToListAsync(ct);

        // Keep candidate priority order (selected org first) so inheritance dedup prefers nearer scopes.
        foreach (var candidate in candidates)
        {
            var org = orgs.FirstOrDefault(o => o.Id == candidate);
            if (org is null) continue;
            Add(org.Id);
            foreach (var ancestor in ParseGuidArray(org.AncestorIdsJson)) Add(ancestor);
        }
        if (readable.Count == 0) foreach (var c in candidates) Add(c);
        return readable;
    }

    private static IEnumerable<Guid> ParseGuidArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(json); } catch { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g)) yield return g;
        }
    }

    // ---- appearance sanitation (upstream normalizeDictionaryColor / normalizeDictionaryIcon) ------

    public static string? NormalizeColor(string? input)
    {
        if (input is null) return null;
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        var m = HexColor.Match(trimmed);
        return m.Success ? "#" + m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static string? NormalizeIcon(string? input)
    {
        if (input is null) return null;
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > 48 ? trimmed[..48] : trimmed;
    }

    // ---- role-type usage (upstream lib/roleTypeUsage.ts) -----------------------------------------

    public readonly record struct RoleTypeUsage(int Total, int OwnerAssignments, int RelationshipAssignments);

    /// <summary>Count owner + relationship assignments for a <c>person_company_role</c> value across the
    /// org and its descendants (upstream <c>loadRoleTypeUsage</c>).</summary>
    public static async Task<RoleTypeUsage> LoadRoleTypeUsageAsync(
        AppDbContext db, Guid tenantId, Guid organizationId, string value, CancellationToken ct = default)
    {
        var scope = new List<Guid> { organizationId };
        var org = await db.Set<Organization>().AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Id == organizationId && o.DeletedAt == null)
            .Select(o => o.DescendantIdsJson).FirstOrDefaultAsync(ct);
        foreach (var d in ParseGuidArray(org)) if (!scope.Contains(d)) scope.Add(d);

        var owner = await db.Set<CustomerEntityRole>().AsNoTracking()
            .CountAsync(r => r.TenantId == tenantId && scope.Contains(r.OrganizationId) && r.RoleType == value, ct);
        var relationship = await db.Set<CustomerPersonCompanyRole>().AsNoTracking()
            .CountAsync(r => r.TenantId == tenantId && scope.Contains(r.OrganizationId) && r.RoleValue == value, ct);
        return new RoleTypeUsage(owner + relationship, owner, relationship);
    }
}
