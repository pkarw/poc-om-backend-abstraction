namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Wildcard-aware feature matching, a 1:1 port of upstream
/// packages/shared/src/lib/auth/featureMatch.ts (matchFeature / hasAllFeatures).
/// </summary>
public static class FeatureMatch
{
    /// <summary>
    /// True when <paramref name="granted"/> satisfies <paramref name="required"/>.
    /// <c>*</c> matches everything; <c>prefix.*</c> matches the exact prefix and anything
    /// under <c>prefix.</c>; otherwise an exact string match is required.
    /// </summary>
    public static bool Match(string required, string granted)
    {
        if (granted == "*") return true;
        if (granted.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = granted[..^2];
            return required == prefix || required.StartsWith(prefix + ".", StringComparison.Ordinal);
        }
        return granted == required;
    }

    /// <summary>True when every required feature is satisfied by the granted set (empty required =&gt; true).</summary>
    public static bool HasAll(IReadOnlyList<string> required, IReadOnlyList<string> granted)
    {
        if (required.Count == 0) return true;
        if (granted.Count == 0) return false;
        return required.All(req => granted.Any(g => Match(req, g)));
    }

    /// <summary>Convenience overload used by grant checks: does the granted set satisfy a single feature.</summary>
    public static bool HasFeature(IReadOnlyList<string> granted, string feature) =>
        granted.Any(g => Match(feature, g));
}
