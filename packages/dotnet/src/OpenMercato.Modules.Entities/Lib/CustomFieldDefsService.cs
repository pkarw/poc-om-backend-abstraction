using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>
/// Scope-aware custom-field-definition lookups shared by the value read/write paths and validation —
/// the port of the repeated "load defs for entity, pick the most specific per key" block in
/// upstream <c>helpers.ts</c> / <c>validation.ts</c> / <c>custom-fields.ts</c>.
///
/// Scope precedence (upstream <c>scopeScore</c>): tenant match scores 2, org match scores 1; the
/// highest score wins, ties broken by newest <c>updatedAt</c>.
/// </summary>
public static class CustomFieldDefsService
{
    /// <summary>Load the active defs for <paramref name="entityId"/> visible in the (tenant, org) scope,
    /// reduced to the winning definition per key.</summary>
    public static async Task<Dictionary<string, CustomFieldDef>> LoadWinningDefsAsync(
        AppDbContext db, string entityId, Guid? tenantId, Guid? organizationId, CancellationToken ct = default)
    {
        var defs = await db.Set<CustomFieldDef>()
            .Where(d => d.EntityId == entityId && d.IsActive && d.DeletedAt == null)
            .Where(d => d.TenantId == null || d.TenantId == tenantId)
            .Where(d => d.OrganizationId == null || d.OrganizationId == organizationId)
            .ToListAsync(ct);

        var byKey = new Dictionary<string, CustomFieldDef>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            if (!byKey.TryGetValue(d.Key, out var existing)) { byKey[d.Key] = d; continue; }
            var ns = ScopeScore(d);
            var es = ScopeScore(existing);
            if (ns > es) { byKey[d.Key] = d; continue; }
            if (ns < es) continue;
            if (d.UpdatedAt >= existing.UpdatedAt) byKey[d.Key] = d;
        }
        return byKey;
    }

    public static int ScopeScore(CustomFieldDef def) =>
        (def.TenantId is not null ? 2 : 0) + (def.OrganizationId is not null ? 1 : 0);

    /// <summary>Parse a stored jsonb config into a <see cref="JsonElement"/> (null-safe).</summary>
    public static JsonElement? ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Whether the def's config declares <c>multi: true</c>.</summary>
    public static bool IsMulti(CustomFieldDef def)
    {
        var cfg = ParseConfig(def.ConfigJson);
        return cfg is { ValueKind: JsonValueKind.Object } c
            && c.TryGetProperty("multi", out var m)
            && m.ValueKind == JsonValueKind.True;
    }
}
