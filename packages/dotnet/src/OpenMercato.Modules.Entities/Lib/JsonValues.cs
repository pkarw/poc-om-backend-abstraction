using System.Text.Json;

namespace OpenMercato.Modules.Entities.Lib;

/// <summary>Helpers converting <see cref="JsonElement"/> payload values to CLR primitives/arrays.</summary>
public static class JsonValues
{
    /// <summary>Convert a JSON value to a CLR object (string/long/double/bool/null or List for arrays).</summary>
    public static object? ToClr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.Array => el.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => el.GetRawText(),
        _ => null,
    };
}
