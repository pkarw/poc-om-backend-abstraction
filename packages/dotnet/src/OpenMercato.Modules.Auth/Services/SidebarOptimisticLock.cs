using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Port of the OSS command-level optimistic-lock guard
/// (<c>packages/shared/src/lib/crud/optimistic-lock-command.ts</c> +
/// <c>optimistic-lock.ts</c>). The client sends the expected <c>updated_at</c> via the
/// <c>x-om-ext-optimistic-lock-expected-updated-at</c> header; on mismatch the caller returns the
/// structured 409 <c>{error:'record_modified', code:'optimistic_lock_conflict', currentUpdatedAt, expectedUpdatedAt}</c>.
/// Strictly additive / fail-open: a no-op when the env disables the guard for the resource kind, or
/// when either side is missing/unparseable. Scoped by <c>OM_OPTIMISTIC_LOCK</c>.
/// </summary>
public static class SidebarOptimisticLock
{
    public const string HeaderName = "x-om-ext-optimistic-lock-expected-updated-at";
    public const string ConflictCode = "optimistic_lock_conflict";
    public const string ConflictError = "record_modified";
    public const string EnvVar = "OM_OPTIMISTIC_LOCK";

    private static readonly HashSet<string> OffTokens = new(StringComparer.Ordinal) { "off", "false", "0", "no" };

    /// <summary>
    /// Returns a 409 conflict body when the guard is enabled for <paramref name="resourceKind"/> and the
    /// expected/current versions disagree; otherwise null (proceed). <paramref name="current"/> is the
    /// row's current <c>updated_at</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? Check(
        string resourceKind,
        DateTimeOffset? current,
        HttpRequest request)
    {
        if (!IsResourceEnabled(resourceKind)) return null;

        var expectedRaw = ReadExpected(request);
        var expectedIso = NormalizeIso(expectedRaw);
        if (expectedIso is null) return null;

        var currentIso = current.HasValue ? SidebarJson.ToIso(current.Value) : null;
        if (currentIso is null) return null;

        if (string.Equals(currentIso, expectedIso, StringComparison.Ordinal)) return null;

        return new Dictionary<string, object?>
        {
            ["error"] = ConflictError,
            ["code"] = ConflictCode,
            ["currentUpdatedAt"] = currentIso,
            ["expectedUpdatedAt"] = expectedIso,
        };
    }

    private static string? ReadExpected(HttpRequest request)
    {
        var value = request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static bool IsResourceEnabled(string resourceKind)
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (raw is null) return true; // default ON (mode 'all')
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return true;

        var tokens = trimmed.Split(',')
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0) return true;
        if (tokens.Any(OffTokens.Contains)) return false;
        if (tokens.Contains("all")) return true;
        return tokens.Contains(resourceKind.ToLowerInvariant());
    }

    /// <summary>Port of <c>normalizeIsoToken</c> (Date.parse → toISOString), null when unparseable.</summary>
    private static string? NormalizeIso(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return null;
        return SidebarJson.ToIso(dt);
    }
}
