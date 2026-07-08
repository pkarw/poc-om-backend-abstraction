using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>Outcome counters for an install-from-CE run (upstream <c>EnsureFieldDefinitionsResult</c>).</summary>
public sealed record InstallCeResult(int Created, int Updated, int Unchanged)
{
    public static InstallCeResult operator +(InstallCeResult a, InstallCeResult b) =>
        new(a.Created + b.Created, a.Updated + b.Updated, a.Unchanged + b.Unchanged);
}

/// <summary>
/// The install-from-CE engine — the port of upstream <c>lib/install-from-ce.ts</c> +
/// <c>lib/field-definitions.ts::ensureCustomFieldDefinitions</c>. Reads every module's declared
/// <see cref="IModule.CustomFieldSets"/> (aggregated by <see cref="ModuleRegistry.AllCustomFieldSets"/>)
/// and upserts a <c>custom_field_defs</c> row per field for a given (tenant, org) scope. Idempotent:
/// re-runs update kind/config only when they changed, otherwise no-op.
///
/// This is how a customer's CE field-set declarations become real, queryable field definitions. It is
/// invoked at seed/init time (see <see cref="InitTenantIntegration"/>) and by the
/// <c>entities install-ce</c> CLI command.
/// </summary>
public static class InstallFromCe
{
    /// <summary>Install all module-declared field sets for a single scope.</summary>
    public static async Task<InstallCeResult> InstallAsync(
        AppDbContext db,
        ModuleRegistry registry,
        Guid? tenantId,
        Guid? organizationId,
        bool createOnly = false,
        CancellationToken ct = default)
    {
        // Aggregate field sets by entity id; last field declaration per key wins (resolveFields).
        var byEntity = registry.AllCustomFieldSets
            .GroupBy(s => s.EntityId, StringComparer.Ordinal);

        var result = new InstallCeResult(0, 0, 0);
        var now = DateTimeOffset.UtcNow;
        var dirty = false;

        foreach (var group in byEntity)
        {
            var entityId = group.Key;
            var fieldsByKey = new Dictionary<string, CustomFieldDefinition>(StringComparer.Ordinal);
            foreach (var set in group)
                foreach (var field in set.Fields)
                    fieldsByKey[field.Key] = field;

            var keys = fieldsByKey.Keys.ToList();
            var existingDefs = await db.Set<CustomFieldDef>()
                .Where(d => d.EntityId == entityId
                            && d.TenantId == tenantId
                            && d.OrganizationId == organizationId
                            && keys.Contains(d.Key))
                .ToListAsync(ct);
            var existingByKey = existingDefs.ToDictionary(d => d.Key, StringComparer.Ordinal);

            foreach (var field in fieldsByKey.Values.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var configJson = BuildConfigJson(field);
                if (!existingByKey.TryGetValue(field.Key, out var existing))
                {
                    db.Set<CustomFieldDef>().Add(new CustomFieldDef
                    {
                        Id = Guid.NewGuid(),
                        EntityId = entityId,
                        OrganizationId = organizationId,
                        TenantId = tenantId,
                        Key = field.Key,
                        Kind = field.Kind,
                        ConfigJson = configJson,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    dirty = true;
                    result += new InstallCeResult(1, 0, 0);
                    continue;
                }

                if (createOnly) { result += new InstallCeResult(0, 0, 1); continue; }

                var kindChanged = existing.Kind != field.Kind;
                var configChanged = !ConfigEquals(existing.ConfigJson, configJson);
                var needsActivation = !existing.IsActive || existing.DeletedAt != null;
                if (!kindChanged && !configChanged && !needsActivation)
                {
                    result += new InstallCeResult(0, 0, 1);
                    continue;
                }

                existing.Kind = field.Kind;
                existing.ConfigJson = configJson;
                existing.IsActive = true;
                existing.UpdatedAt = now;
                existing.DeletedAt = null;
                dirty = true;
                result += new InstallCeResult(0, 1, 0);
            }
        }

        if (dirty) await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Build the canonical jsonb config from a CE field declaration (upstream
    /// <c>ensureCustomFieldDefinitions</c> CONFIG_PASSTHROUGH_KEYS). Keys are emitted in sorted order so
    /// re-runs compare byte-stable.
    /// </summary>
    internal static string BuildConfigJson(CustomFieldDefinition field)
    {
        // Sorted keys → deterministic serialization (parity with upstream normalizeValue for checksums).
        var map = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["label"] = string.IsNullOrEmpty(field.Label) ? field.Key : field.Label,
            ["multi"] = field.Multi,
            ["required"] = field.Required,
        };
        if (field.Options is { Length: > 0 })
            map["options"] = field.Options;
        return JsonSerializer.Serialize(map);
    }

    private static bool ConfigEquals(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na == nb;
    }

    private static string Normalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "null";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Canonicalize(doc.RootElement);
        }
        catch { return json!; }
    }

    private static string Canonicalize(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var props = el.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonicalize(p.Value));
                return "{" + string.Join(",", props) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", el.EnumerateArray().Select(Canonicalize)) + "]";
            default:
                return el.GetRawText();
        }
    }
}
