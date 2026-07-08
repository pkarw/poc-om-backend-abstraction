using System.Text.Json;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>
/// Port of <c>shared/lib/crud/custom-fields.ts::splitCustomFieldPayload</c>. Extracts the custom-field
/// inputs a write body carries — <c>cf_&lt;key&gt;</c>, <c>cf:&lt;key&gt;</c>, a <c>customValues</c> map, and a
/// <c>customFields</c> array/map — into a flat bare-key dictionary (the request→storage key convention).
/// </summary>
public static class CustomFieldPayload
{
    public static Dictionary<string, object?> ExtractCustom(JsonElement body)
    {
        var custom = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) return custom;

        foreach (var prop in body.EnumerateObject())
        {
            var key = prop.Name;
            if (key == "customFields")
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in prop.Value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        if (!entry.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
                        var entryKey = k.GetString()!.Trim();
                        if (entryKey.Length == 0) continue;
                        custom[entryKey] = entry.TryGetProperty("value", out var v) ? JsonValues.ToClr(v) : null;
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var ck in prop.Value.EnumerateObject())
                    {
                        var nk = ck.Name.Trim();
                        if (nk.Length == 0) continue;
                        custom[nk] = JsonValues.ToClr(ck.Value);
                    }
                }
                continue;
            }
            if (key == "customValues" && prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var ck in prop.Value.EnumerateObject())
                    custom[ck.Name] = JsonValues.ToClr(ck.Value);
                continue;
            }
            if (key.StartsWith("cf_", StringComparison.Ordinal))
            {
                custom[key[3..]] = JsonValues.ToClr(prop.Value);
                continue;
            }
            if (key.StartsWith("cf:", StringComparison.Ordinal))
            {
                custom[key[3..]] = JsonValues.ToClr(prop.Value);
                continue;
            }
            // base fields are ignored by the custom-field codec
        }
        return custom;
    }
}
