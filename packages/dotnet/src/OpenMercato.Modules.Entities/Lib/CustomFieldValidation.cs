using System.Globalization;
using System.Text.Json;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>A resolved custom-field definition + its parsed validation rules, used by the validator.</summary>
public sealed record DefLike(string Key, string Kind, JsonElement? ConfigJson);

/// <summary>Result of value validation — the port of <c>{ ok, fieldErrors }</c>.</summary>
public sealed record ValidateResult(bool Ok, IReadOnlyDictionary<string, string> FieldErrors);

/// <summary>
/// Port of <c>shared/modules/entities/validation.ts::validateValuesAgainstDefs</c> — evaluates the
/// per-kind/per-rule validation declared in each definition's <c>configJson.validation</c>, plus the
/// EAV mass-assignment guards (<c>rejectUndeclaredKeys</c> + the per-record key cap). Errors are keyed
/// by <c>cf_&lt;key&gt;</c> exactly as upstream (the request-side key convention).
/// </summary>
public static class CustomFieldValidation
{
    public const int MaxCustomFieldKeysPerRecord = 128;
    public const string UnknownCustomFieldError = "[internal] Unknown custom field";
    public const string TooManyCustomFieldsError = "[internal] Too many custom fields";

    public static ValidateResult ValidateValuesAgainstDefs(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<DefLike> defs,
        bool rejectUndeclaredKeys = false)
    {
        var errors = new Dictionary<string, string>();

        if (rejectUndeclaredKeys)
        {
            var allowed = new HashSet<string>(defs.Select(d => d.Key), StringComparer.Ordinal);
            foreach (var key in values.Keys)
            {
                if (values[key] is null && !values.ContainsKey(key)) continue; // present-but-undefined n/a in C#
                if (!allowed.Contains(key))
                    errors[$"cf_{key}"] = UnknownCustomFieldError;
            }
        }

        if (values.Count > MaxCustomFieldKeysPerRecord)
            errors["_customFields"] = TooManyCustomFieldsError;

        foreach (var def in defs)
        {
            if (def.ConfigJson is not { } cfg) continue;
            if (cfg.ValueKind != JsonValueKind.Object) continue;
            if (!cfg.TryGetProperty("validation", out var rulesEl) || rulesEl.ValueKind != JsonValueKind.Array) continue;
            values.TryGetValue(def.Key, out var value);
            foreach (var rule in rulesEl.EnumerateArray())
            {
                var msg = EvalRule(rule, value, def.Kind);
                if (msg is not null)
                {
                    errors[$"cf_{def.Key}"] = msg;
                    break;
                }
            }
        }

        return new ValidateResult(errors.Count == 0, errors);
    }

    private static bool IsEmpty(object? v)
    {
        if (v is null) return true;
        if (v is string s) return s.Trim().Length == 0;
        if (v is IEnumerable<object?> arr) return !arr.Any();
        return false;
    }

    private static string? EvalRule(JsonElement rule, object? value, string kind)
    {
        if (rule.ValueKind != JsonValueKind.Object) return null;
        if (!rule.TryGetProperty("rule", out var ruleName) || ruleName.ValueKind != JsonValueKind.String) return null;
        var message = rule.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Invalid value";

        if (ruleName.GetString() == "required")
            return IsEmpty(value) ? message : null;

        // Multi-value: apply every non-required rule per element (validation.ts::evalRule).
        if (value is IEnumerable<object?> arr && value is not string)
        {
            foreach (var item in arr)
            {
                var r = EvalScalar(rule, ruleName.GetString()!, item, kind, message);
                if (r is not null) return r;
            }
            return null;
        }
        return EvalScalar(rule, ruleName.GetString()!, value, kind, message);
    }

    private static string? EvalScalar(JsonElement rule, string ruleName, object? value, string kind, string message)
    {
        switch (ruleName)
        {
            case "required":
                return IsEmpty(value) ? message : null;
            case "date":
                if (IsEmpty(value)) return null;
                return DateTime.TryParse(ToStr(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : message;
            case "integer":
                if (IsEmpty(value)) return null;
                return long.TryParse(ToStr(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : message;
            case "float":
                if (IsEmpty(value)) return null;
                return double.TryParse(ToStr(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && !double.IsInfinity(f) ? null : message;
            case "lt": return CmpNum(value, rule, message, (a, b) => a < b);
            case "lte": return CmpNum(value, rule, message, (a, b) => a <= b);
            case "gt": return CmpNum(value, rule, message, (a, b) => a > b);
            case "gte": return CmpNum(value, rule, message, (a, b) => a >= b);
            case "eq":
                if (IsEmpty(value)) return null;
                return ParamEquals(value, rule) ? null : message;
            case "ne":
                if (IsEmpty(value)) return null;
                return !ParamEquals(value, rule) ? null : message;
            case "regex":
                if (IsEmpty(value)) return null;
                var pattern = rule.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
                try { return System.Text.RegularExpressions.Regex.IsMatch(ToStr(value)!, pattern) ? null : message; }
                catch { return message; }
            default:
                return null;
        }
    }

    private static string? CmpNum(object? value, JsonElement rule, string message, Func<double, double, bool> ok)
    {
        if (IsEmpty(value)) return null;
        if (!double.TryParse(ToStr(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return message;
        if (!rule.TryGetProperty("param", out var p) || !p.TryGetDouble(out var param)) return message;
        return ok(v, param) ? null : message;
    }

    private static bool ParamEquals(object? value, JsonElement rule)
    {
        if (!rule.TryGetProperty("param", out var p)) return false;
        return ToStr(value) == (p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText());
    }

    private static string? ToStr(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}
