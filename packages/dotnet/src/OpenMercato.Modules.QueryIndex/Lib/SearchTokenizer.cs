using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenMercato.Modules.QueryIndex.Lib;

/// <summary>
/// Tokenized-search configuration — the byte-for-byte port of upstream
/// <c>packages/shared/src/lib/search/config.ts</c> (<c>resolveSearchConfig</c>). Env-driven with the same
/// keys and defaults so token hashes computed here match the TypeScript build exactly.
/// </summary>
public sealed record SearchConfig
{
    public bool Enabled { get; init; } = true;
    public int MinTokenLength { get; init; } = 3;
    public bool EnablePartials { get; init; } = true;
    /// <summary><c>sha256</c> | <c>sha1</c> | <c>md5</c> (upstream default sha256).</summary>
    public string HashAlgorithm { get; init; } = "sha256";
    public bool StoreRawTokens { get; init; } = false;
    public IReadOnlyList<string> BlocklistedFields { get; init; } = DefaultBlocklist;

    // config.ts DEFAULT_BLOCKLIST
    private static readonly string[] DefaultBlocklist = { "password", "token", "secret", "hash" };

    /// <summary>Resolve config from environment (config.ts::resolveSearchConfig, same keys/defaults).</summary>
    public static SearchConfig Resolve()
    {
        return new SearchConfig
        {
            Enabled = ParseBool(Env("OM_SEARCH_ENABLED"), true),
            MinTokenLength = ParseNumber(Env("OM_SEARCH_MIN_LEN"), 3, 1),
            EnablePartials = ParseBool(Env("OM_SEARCH_ENABLE_PARTIAL"), true),
            HashAlgorithm = ParseHashAlgorithm(Env("OM_SEARCH_HASH_ALGO")),
            StoreRawTokens = ParseBool(Env("OM_SEARCH_STORE_RAW_TOKENS"), false),
            BlocklistedFields = ResolveBlocklist(Env("OM_SEARCH_FIELD_BLOCKLIST")),
        };
    }

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    // boolean.ts parseBooleanWithDefault
    private static readonly HashSet<string> TrueValues = new(StringComparer.Ordinal) { "1", "true", "yes", "y", "on", "enable", "enabled" };
    private static readonly HashSet<string> FalseValues = new(StringComparer.Ordinal) { "0", "false", "no", "n", "off", "disable", "disabled" };

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (raw is null) return fallback;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return fallback;
        var normalized = trimmed.ToLowerInvariant();
        if (TrueValues.Contains(normalized)) return true;
        if (FalseValues.Contains(normalized)) return false;
        return fallback;
    }

    private static int ParseNumber(string? raw, int fallback, int min)
    {
        if (raw is null) return fallback;
        // config.ts uses Number.parseInt(raw, 10): parse a leading integer, ignore trailing garbage.
        var m = Regex.Match(raw.TrimStart(), @"^[+-]?\d+");
        if (!m.Success) return fallback;
        if (!int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
        if (value < min) return fallback;
        return value;
    }

    private static string ParseHashAlgorithm(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "sha1") return "sha1";
        if (value == "md5") return "md5";
        return "sha256";
    }

    private static IReadOnlyList<string> ResolveBlocklist(string? raw)
    {
        // config.ts: split(',') → trim → drop empties → dedupe → lower → concat(DEFAULT) → dedupe.
        var custom = (raw ?? string.Empty)
            .Split(',')
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(e => e.ToLowerInvariant());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var entry in custom.Concat(DefaultBlocklist))
            if (seen.Add(entry)) result.Add(entry);
        return result;
    }
}

/// <summary>
/// Tokenizer — the byte-for-byte port of upstream <c>packages/shared/src/lib/search/tokenize.ts</c>
/// (<c>tokenizeText</c> / <c>hashToken</c>). Reproduces the normalization (NFKD, strip combining marks,
/// <c>%</c>/<c>_</c> → space, lowercase), the <c>[^a-zA-Z0-9]+</c> split, the prefix-partial expansion,
/// and the <c>hash(algo).update(token).digest('hex')</c> token hash so hashes match the TypeScript build.
/// </summary>
public static class SearchTokenizer
{
    private static readonly Regex CombiningMarks = new("[\\u0300-\\u036f]", RegexOptions.Compiled);
    // tokenize.ts split(/[^a-z0-9]+/i): the /i flag on a negated class also excludes A-Z ⇒ [^a-zA-Z0-9]+.
    private static readonly Regex Splitter = new("[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public sealed record TokenizationResult(IReadOnlyList<string> Tokens, IReadOnlyList<string> Hashes);

    // tokenize.ts::normalizeText
    private static string NormalizeText(string text)
    {
        var stripped = CombiningMarks.Replace(text.Normalize(NormalizationForm.FormKD), string.Empty);
        return stripped.Replace('%', ' ').Replace('_', ' ').ToLowerInvariant();
    }

    // tokenize.ts::splitTokens
    private static List<string> SplitTokens(string text, int minLength)
    {
        var result = new List<string>();
        foreach (var token in Splitter.Split(NormalizeText(text)))
            if (token.Length >= minLength) result.Add(token);
        return result;
    }

    // tokenize.ts::expandToken
    private static IEnumerable<string> ExpandToken(string token, SearchConfig config)
    {
        if (!config.EnablePartials) { yield return token; yield break; }
        for (var i = config.MinTokenLength; i <= token.Length; i += 1)
            yield return token.Substring(0, i);
    }

    // tokenize.ts::hashToken
    public static string HashToken(string token, SearchConfig config)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        byte[] digest = config.HashAlgorithm switch
        {
            "sha1" => SHA1.HashData(bytes),
            "md5" => MD5.HashData(bytes),
            _ => SHA256.HashData(bytes),
        };
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    // tokenize.ts::tokenizeText
    public static TokenizationResult Tokenize(string text, SearchConfig config)
    {
        var baseTokens = SplitTokens(text, config.MinTokenLength);
        // flatMap(expand) → dedupe preserving insertion order (new Set(...)) → filter len >= minLength.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var baseToken in baseTokens)
            foreach (var expanded in ExpandToken(baseToken, config))
                if (seen.Add(expanded) && expanded.Length >= config.MinTokenLength)
                    tokens.Add(expanded);
        var hashes = new List<string>(tokens.Count);
        foreach (var token in tokens) hashes.Add(HashToken(token, config));
        return new TokenizationResult(tokens, hashes);
    }
}
