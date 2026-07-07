using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMercato.Modules.Auth.Services;

// PARITY-TODO: upstream returns Zod's `flatten()` verbatim in the `details` field. This hand-rolled
// validator reproduces the flatten *shape* ({formErrors, fieldErrors}) and the load-bearing
// behaviors (types, min/max lengths, uuid checks, the scope-vs-role superRefine) but the individual
// message strings are approximations of Zod's copy. Status codes and envelope keys are exact.

/// <summary>Raw, validated settings fields (any of which may be absent).</summary>
public sealed class SidebarSettingsInput
{
    public int? Version;
    public List<string>? GroupOrder;
    public Dictionary<string, string>? GroupLabels;
    public Dictionary<string, string>? ItemLabels;
    public List<string>? HiddenItems;
    public Dictionary<string, List<string>>? ItemOrder;

    /// <summary>Materialize to a normalized <see cref="SidebarSettings"/> (variant path — no pre-trim).</summary>
    public SidebarSettings ToSettings() => SidebarSettings.NormalizeCore(
        Version,
        GroupOrder,
        GroupLabels,
        ItemLabels,
        HiddenItems,
        ItemOrder?.Select(kv => new KeyValuePair<string, IEnumerable<string>>(kv.Key, kv.Value)));
}

public sealed class CreateVariantInput
{
    public string? Name;
    public SidebarSettingsInput? Settings;
    public bool? IsActive;
}

public sealed class PreferenceScope
{
    public required string Type;      // "user" | "role"
    public Guid? RoleId;              // present when Type == "role"
    public string? RoleIdRaw;
}

public sealed class PreferencesInput
{
    public SidebarSettingsInput Settings = new();
    public List<string>? ApplyToRoles;   // raw uuid strings (case-sensitive downstream)
    public List<string>? ClearRoleIds;
    public PreferenceScope? Scope;
}

public sealed record SidebarValidationResult<T>(bool Ok, T? Value, IReadOnlyDictionary<string, object?>? Details);

public static class SidebarValidation
{
    private static readonly Regex UuidRegex = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    private sealed class Errors
    {
        public readonly List<string> Form = new();
        public readonly Dictionary<string, List<string>> Fields = new();
        public bool Any => Form.Count > 0 || Fields.Count > 0;

        public void Field(string name, string message)
        {
            if (!Fields.TryGetValue(name, out var list)) Fields[name] = list = new List<string>();
            list.Add(message);
        }

        public IReadOnlyDictionary<string, object?> ToDetails() => new Dictionary<string, object?>
        {
            ["formErrors"] = Form,
            ["fieldErrors"] = Fields,
        };
    }

    public static SidebarValidationResult<CreateVariantInput> ValidateVariant(JsonElement body)
    {
        var errors = new Errors();
        var result = new CreateVariantInput();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Form.Add("Expected object, received " + body.ValueKind.ToString().ToLowerInvariant());
            return new SidebarValidationResult<CreateVariantInput>(false, null, errors.ToDetails());
        }

        if (TryGet(body, "name", out var nameEl))
        {
            if (nameEl.ValueKind != JsonValueKind.String) errors.Field("name", "Expected string");
            else
            {
                var trimmed = nameEl.GetString()!.Trim();
                if (trimmed.Length < 1) errors.Field("name", "String must contain at least 1 character(s)");
                else if (trimmed.Length > 120) errors.Field("name", "String must contain at most 120 character(s)");
                else result.Name = trimmed;
            }
        }

        if (TryGet(body, "settings", out var settingsEl))
            result.Settings = ValidateSettings(settingsEl, errors, bucket: "settings");

        if (TryGet(body, "isActive", out var activeEl))
        {
            if (activeEl.ValueKind is JsonValueKind.True or JsonValueKind.False) result.IsActive = activeEl.GetBoolean();
            else errors.Field("isActive", "Expected boolean");
        }

        return errors.Any
            ? new SidebarValidationResult<CreateVariantInput>(false, null, errors.ToDetails())
            : new SidebarValidationResult<CreateVariantInput>(true, result, null);
    }

    public static SidebarValidationResult<PreferencesInput> ValidatePreferences(JsonElement body)
    {
        var errors = new Errors();
        var result = new PreferencesInput();
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Form.Add("Expected object, received " + body.ValueKind.ToString().ToLowerInvariant());
            return new SidebarValidationResult<PreferencesInput>(false, null, errors.ToDetails());
        }

        // Settings fields live at the top level of the preferences schema.
        result.Settings = ValidateSettings(body, errors, bucket: null);

        result.ApplyToRoles = ReadUuidArray(body, "applyToRoles", errors);
        result.ClearRoleIds = ReadUuidArray(body, "clearRoleIds", errors);

        if (TryGet(body, "scope", out var scopeEl))
            result.Scope = ValidateScope(scopeEl, errors);

        // superRefine: applyToRoles/clearRoleIds are only valid in user scope.
        var scopeType = result.Scope?.Type ?? "user";
        if (scopeType == "role")
        {
            if ((result.ApplyToRoles?.Count ?? 0) > 0)
                errors.Field("applyToRoles", "applyToRoles is only valid when scope.type === \"user\"");
            if ((result.ClearRoleIds?.Count ?? 0) > 0)
                errors.Field("clearRoleIds", "clearRoleIds is only valid when scope.type === \"user\"");
        }

        return errors.Any
            ? new SidebarValidationResult<PreferencesInput>(false, null, errors.ToDetails())
            : new SidebarValidationResult<PreferencesInput>(true, result, null);
    }

    /// <summary>
    /// Validate the shared settings fields on <paramref name="obj"/>. When <paramref name="bucket"/> is
    /// set every error is filed under that field name (variants nest settings under "settings"); when
    /// null each error is filed under its own field name (preferences keeps them top-level).
    /// </summary>
    private static SidebarSettingsInput ValidateSettings(JsonElement obj, Errors errors, string? bucket)
    {
        var input = new SidebarSettingsInput();
        if (obj.ValueKind != JsonValueKind.Object)
        {
            errors.Field(bucket ?? "settings", "Expected object");
            return input;
        }

        string Bucket(string field) => bucket ?? field;

        if (TryGet(obj, "version", out var versionEl))
        {
            if (versionEl.ValueKind != JsonValueKind.Number) errors.Field(Bucket("version"), "Expected number");
            else if (!versionEl.TryGetInt32(out var iv)) errors.Field(Bucket("version"), "Expected integer");
            else if (iv <= 0) errors.Field(Bucket("version"), "Number must be greater than 0");
            else input.Version = iv;
        }

        input.GroupOrder = ReadStringArray(obj, "groupOrder", errors, Bucket("groupOrder"), 200);
        input.HiddenItems = ReadStringArray(obj, "hiddenItems", errors, Bucket("hiddenItems"), 500);
        input.GroupLabels = ReadStringRecord(obj, "groupLabels", errors, Bucket("groupLabels"));
        input.ItemLabels = ReadStringRecord(obj, "itemLabels", errors, Bucket("itemLabels"));
        input.ItemOrder = ReadStringArrayRecord(obj, "itemOrder", errors, Bucket("itemOrder"));

        return input;
    }

    private static PreferenceScope? ValidateScope(JsonElement el, Errors errors)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Field("scope", "Invalid input");
            return null;
        }
        var type = TryGet(el, "type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;
        if (type == "user")
            return new PreferenceScope { Type = "user" };
        if (type == "role")
        {
            if (TryGet(el, "roleId", out var roleEl) && roleEl.ValueKind == JsonValueKind.String &&
                UuidRegex.IsMatch(roleEl.GetString()!))
            {
                var raw = roleEl.GetString()!;
                return new PreferenceScope { Type = "role", RoleId = Guid.Parse(raw), RoleIdRaw = raw };
            }
            errors.Field("scope", "Invalid uuid");
            return new PreferenceScope { Type = "role" };
        }
        errors.Field("scope", "Invalid input");
        return null;
    }

    private static List<string>? ReadStringArray(JsonElement obj, string field, Errors errors, string bucket, int max)
    {
        if (!TryGet(obj, field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Array) { errors.Field(bucket, "Expected array"); return null; }
        var list = new List<string>();
        var count = 0;
        foreach (var item in el.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.String) { errors.Field(bucket, "Expected string"); continue; }
            var s = item.GetString()!;
            if (s.Length < 1) { errors.Field(bucket, "String must contain at least 1 character(s)"); continue; }
            list.Add(s);
        }
        if (count > max) errors.Field(bucket, $"Array must contain at most {max} element(s)");
        return list;
    }

    private static Dictionary<string, string>? ReadStringRecord(JsonElement obj, string field, Errors errors, string bucket)
    {
        if (!TryGet(obj, field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Object) { errors.Field(bucket, "Expected object"); return null; }
        var map = new Dictionary<string, string>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name.Length < 1) { errors.Field(bucket, "String must contain at least 1 character(s)"); continue; }
            if (prop.Value.ValueKind != JsonValueKind.String) { errors.Field(bucket, "Expected string"); continue; }
            var v = prop.Value.GetString()!;
            if (v.Length < 1) { errors.Field(bucket, "String must contain at least 1 character(s)"); continue; }
            if (v.Length > 120) { errors.Field(bucket, "String must contain at most 120 character(s)"); continue; }
            map[prop.Name] = v;
        }
        return map;
    }

    private static Dictionary<string, List<string>>? ReadStringArrayRecord(JsonElement obj, string field, Errors errors, string bucket)
    {
        if (!TryGet(obj, field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Object) { errors.Field(bucket, "Expected object"); return null; }
        var map = new Dictionary<string, List<string>>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name.Length < 1) { errors.Field(bucket, "String must contain at least 1 character(s)"); continue; }
            if (prop.Value.ValueKind != JsonValueKind.Array) { errors.Field(bucket, "Expected array"); continue; }
            var list = new List<string>();
            var count = 0;
            foreach (var item in prop.Value.EnumerateArray())
            {
                count++;
                if (item.ValueKind != JsonValueKind.String) { errors.Field(bucket, "Expected string"); continue; }
                var s = item.GetString()!;
                if (s.Length < 1) { errors.Field(bucket, "String must contain at least 1 character(s)"); continue; }
                list.Add(s);
            }
            if (count > 500) errors.Field(bucket, "Array must contain at most 500 element(s)");
            map[prop.Name] = list;
        }
        return map;
    }

    private static List<string>? ReadUuidArray(JsonElement obj, string field, Errors errors)
    {
        if (!TryGet(obj, field, out var el)) return null;
        if (el.ValueKind != JsonValueKind.Array) { errors.Field(field, "Expected array"); return null; }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !UuidRegex.IsMatch(item.GetString()!))
            {
                errors.Field(field, "Invalid uuid");
                continue;
            }
            list.Add(item.GetString()!);
        }
        return list;
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null) return true;
        value = default;
        return false;
    }
}
