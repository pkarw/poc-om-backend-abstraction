using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// Query-string coercion helpers mirroring the upstream Zod query schemas
/// (<c>z.coerce.number().min().max().default()</c>, <c>z.string().uuid().optional()</c>). A failed
/// coercion returns false so the hand-written GET handlers can reproduce the empty-envelope quirk.
/// </summary>
internal static class QueryParse
{
    /// <summary>Absent =&gt; (true, null). Present valid uuid =&gt; (true, guid). Present invalid =&gt; false.</summary>
    public static bool OptionalGuid(StringValues raw, out Guid? value)
    {
        value = null;
        var s = raw.Count > 0 ? raw.ToString() : null;
        if (string.IsNullOrEmpty(s)) return true;
        if (Guid.TryParse(s, out var g)) { value = g; return true; }
        return false;
    }

    /// <summary>Absent =&gt; default. Present numeric within [min,max] =&gt; value. Otherwise false.</summary>
    public static bool CoerceIntWithDefault(StringValues raw, int def, int min, int max, out int value)
    {
        value = def;
        var s = raw.Count > 0 ? raw.ToString() : null;
        if (string.IsNullOrEmpty(s)) return true;
        if (!double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) return false;
        if (n < min || n > max) return false;
        value = (int)n;
        if (value < min) return false;
        return true;
    }
}
