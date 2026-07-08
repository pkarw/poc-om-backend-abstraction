using System.Globalization;

namespace OpenMercato.Core.Commands;

/// <summary>
/// OSS opt-in optimistic-locking helper — the port of upstream
/// <c>enforceCommandOptimisticLock</c> / <c>assertOptimisticLock</c>
/// (packages/shared/src/lib/crud/optimistic-lock-command.ts + optimistic-lock.ts). Usable by both the
/// CRUD factory and command handlers.
///
/// Contract (spec 02 R40): a client sends the expected version via the
/// <c>x-om-ext-optimistic-lock-expected-updated-at</c> header (ISO date). On mismatch with the row's
/// current <c>updated_at</c> the helper throws <see cref="CommandHttpException"/> 409 with body
/// <c>{ error: "record_modified", code: "optimistic_lock_conflict", currentUpdatedAt, expectedUpdatedAt }</c>.
///
/// Strictly additive: a no-op when no expected token is present, when the current version is missing,
/// or when the guard is disabled for the resource kind via <c>OM_OPTIMISTIC_LOCK</c>
/// (default ON = all entities; <c>off/false/0/no/disabled/none</c> disables; comma-list = allow-list).
/// </summary>
public static class OptimisticLock
{
    public const string HeaderName = "x-om-ext-optimistic-lock-expected-updated-at";
    public const string ConflictError = "record_modified";
    public const string ConflictCode = "optimistic_lock_conflict";
    public const string EnvVar = "OM_OPTIMISTIC_LOCK";

    /// <summary>The exact 409 body shape (spec 02 Contracts).</summary>
    public sealed record ConflictBody(string error, string code, string currentUpdatedAt, string expectedUpdatedAt);

    public enum Mode { Off, All, Allowlist }

    public sealed record Config(Mode Mode, IReadOnlySet<string> Entities);

    private static readonly HashSet<string> OffTokens =
        new(StringComparer.OrdinalIgnoreCase) { "off", "false", "0", "no", "disabled", "none" };

    /// <summary>Pure parser for <c>OM_OPTIMISTIC_LOCK</c> (default ON). Mirrors upstream <c>parseOptimisticLockEnv</c>.</summary>
    public static Config ParseEnv(string? raw)
    {
        if (raw is null) return new Config(Mode.All, EmptySet);
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return new Config(Mode.All, EmptySet);

        var tokens = trimmed.Split(',')
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0) return new Config(Mode.All, EmptySet);
        if (tokens.Any(t => OffTokens.Contains(t))) return new Config(Mode.Off, EmptySet);
        if (tokens.Contains("all")) return new Config(Mode.All, EmptySet);
        return new Config(Mode.Allowlist, tokens.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private static bool IsResourceLockEnabled(Config config, string resourceKind) => config.Mode switch
    {
        Mode.Off => false,
        Mode.All => true,
        _ => config.Entities.Contains(resourceKind.ToLowerInvariant()),
    };

    /// <summary>Read the expected-version header from a context's headers, or null.</summary>
    public static string? ReadExpected(CommandContext ctx) => ReadExpected(ctx.Headers);

    /// <summary>Read the expected-version header from a header map (case-insensitive), or null.</summary>
    public static string? ReadExpected(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return null;
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, HeaderName, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Command-handler convenience: resolve the expected version from an explicit override or the
    /// context header, then assert against <paramref name="current"/>. Mirrors upstream
    /// <c>enforceCommandOptimisticLock</c>.
    /// </summary>
    public static void Enforce(
        string resourceKind,
        string resourceId,
        DateTime? current,
        CommandContext ctx,
        string? expectedOverride = null,
        string? envValue = null)
    {
        var expected = expectedOverride ?? ReadExpected(ctx);
        Assert(resourceKind, resourceId, expected, current, envValue);
    }

    /// <summary>
    /// Pure version assertion. Throws 409 when expected and current versions disagree; no-op when the
    /// guard is disabled for the resource kind, or when either side is missing/unparseable. Mirrors
    /// upstream <c>assertOptimisticLock</c>.
    /// </summary>
    public static void Assert(
        string resourceKind,
        string resourceId,
        string? expected,
        DateTime? current,
        string? envValue = null)
    {
        var config = ParseEnv(envValue ?? Environment.GetEnvironmentVariable(EnvVar));
        if (!IsResourceLockEnabled(config, resourceKind)) return;

        var expectedIso = ToIsoOrNull(expected);
        if (expectedIso is null) return;

        var currentIso = current is null ? null : ToIso(current.Value);
        if (currentIso is null) return;

        if (currentIso == expectedIso) return;

        throw new CommandHttpException(409, new ConflictBody(ConflictError, ConflictCode, currentIso, expectedIso));
    }

    private static string ToIso(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    private static string? ToIsoOrNull(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (DateTimeOffset.TryParse(token.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
            return ToIso(dto.UtcDateTime);
        return null;
    }
}
