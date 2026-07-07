using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Modules.Auth.Security;
using StackExchange.Redis;

namespace OpenMercato.Modules.Auth.RateLimit;

/// <summary>
/// In-handler, fail-open rate limiter mirroring the upstream two-layer auth limiter
/// (packages/core/src/modules/auth/lib/rateLimitCheck.ts + packages/shared/src/lib/ratelimit).
/// Layer 1 is IP-only; layer 2 (optional) is IP + hashed identifier. Any error (Redis down,
/// missing services) fails open — the request proceeds. Backed by the shared Redis multiplexer
/// with a fixed-window counter (INCR + EXPIRE) and optional block window.
///
/// PARITY note: upstream uses rate-limiter-flexible with a memory insurance limiter; this port uses
/// a Redis fixed window only. The 429 envelope + headers and the points/duration/blockDuration
/// semantics match the contract. Disabled the same ways as upstream: OM_INTEGRATION_TEST,
/// RATE_LIMIT_ENABLED=false, and the OM_TEST_MODE + OM_TEST_AUTH_RATE_LIMIT_MODE=opt-in escape hatch.
/// </summary>
internal static class AuthRateLimiter
{
    public const string ErrorMessage = "Too many requests. Please try again later.";

    public sealed record Rule(int Points, int DurationSec, int BlockDurationSec, string KeyPrefix);

    /// <summary>Build a rule with env overrides RATE_LIMIT_{PREFIX}_POINTS/_DURATION/_BLOCK_DURATION.</summary>
    public static Rule Configure(string envPrefix, int points, int durationSec, int blockDurationSec, string keyPrefix) =>
        new(
            Points: ReadInt($"RATE_LIMIT_{envPrefix}_POINTS", points),
            DurationSec: ReadInt($"RATE_LIMIT_{envPrefix}_DURATION", durationSec),
            BlockDurationSec: ReadInt($"RATE_LIMIT_{envPrefix}_BLOCK_DURATION", blockDurationSec),
            KeyPrefix: keyPrefix);

    /// <summary>
    /// Run the IP layer and (optionally) the compound layer. Returns a non-null <c>Error</c> IResult
    /// (429 with headers written to the response) when limited, plus the compound key used (for reset).
    /// </summary>
    public static async Task<(IResult? Error, string? CompoundKey)> CheckAsync(
        HttpContext http, Rule ipRule, Rule? compoundRule = null, string? compoundIdentifier = null)
    {
        try
        {
            if (!Enabled(http)) return (null, null);

            var mux = http.RequestServices.GetService<IConnectionMultiplexer>();
            if (mux is null) return (null, null);
            var redis = mux.GetDatabase();

            var clientIp = ClientIp(http);
            if (clientIp is null) return (null, null);

            var ipResult = await ConsumeAsync(redis, ipRule, clientIp);
            if (!ipResult.Allowed) return (Reject(http, ipRule, ipResult), null);

            if (compoundRule is not null && !string.IsNullOrEmpty(compoundIdentifier))
            {
                var enc = http.RequestServices.GetService<EncryptionService>();
                var hash = enc?.ComputeEmailHash(compoundIdentifier) ?? compoundIdentifier;
                var compoundKey = $"{clientIp}:{hash}";
                var compoundResult = await ConsumeAsync(redis, compoundRule, compoundKey);
                if (!compoundResult.Allowed) return (Reject(http, compoundRule, compoundResult), compoundKey);
                return (null, compoundKey);
            }

            return (null, null);
        }
        catch
        {
            return (null, null); // fail open
        }
    }

    /// <summary>Best-effort reset of a compound counter after a successful auth (never throws).</summary>
    public static async Task ResetAsync(HttpContext http, string? compoundKey, Rule rule)
    {
        if (string.IsNullOrEmpty(compoundKey)) return;
        try
        {
            var mux = http.RequestServices.GetService<IConnectionMultiplexer>();
            if (mux is null) return;
            await mux.GetDatabase().KeyDeleteAsync(RedisKey(rule, compoundKey));
        }
        catch
        {
            // best-effort
        }
    }

    private readonly record struct Consumption(bool Allowed, int RemainingPoints, long MsBeforeNext);

    private static async Task<Consumption> ConsumeAsync(IDatabase redis, Rule rule, string key)
    {
        var redisKey = RedisKey(rule, key);
        var count = await redis.StringIncrementAsync(redisKey);
        if (count == 1)
            await redis.KeyExpireAsync(redisKey, TimeSpan.FromSeconds(rule.DurationSec));

        if (count > rule.Points)
        {
            // On first rejection, extend the window to the block duration (if configured).
            if (rule.BlockDurationSec > 0 && count == rule.Points + 1)
                await redis.KeyExpireAsync(redisKey, TimeSpan.FromSeconds(rule.BlockDurationSec));
            var ttl = await redis.KeyTimeToLiveAsync(redisKey);
            var ms = (long)(ttl?.TotalMilliseconds ?? rule.DurationSec * 1000.0);
            return new Consumption(false, 0, ms);
        }

        return new Consumption(true, Math.Max(rule.Points - (int)count, 0), 0);
    }

    private static IResult Reject(HttpContext http, Rule rule, Consumption result)
    {
        var retryAfter = (int)Math.Ceiling(result.MsBeforeNext / 1000.0);
        http.Response.Headers["Retry-After"] = retryAfter.ToString();
        http.Response.Headers["X-RateLimit-Limit"] = rule.Points.ToString();
        http.Response.Headers["X-RateLimit-Remaining"] = result.RemainingPoints.ToString();
        http.Response.Headers["X-RateLimit-Reset"] = retryAfter.ToString();
        return Results.Json(new { error = ErrorMessage }, statusCode: 429);
    }

    private static string RedisKey(Rule rule, string key)
    {
        var globalPrefix = Environment.GetEnvironmentVariable("RATE_LIMIT_KEY_PREFIX");
        if (string.IsNullOrEmpty(globalPrefix)) globalPrefix = "rl";
        return $"{globalPrefix}:{rule.KeyPrefix}:{key}";
    }

    private static bool Enabled(HttpContext http)
    {
        if (ParseBool(Environment.GetEnvironmentVariable("OM_INTEGRATION_TEST"), false))
            return false;

        if (Environment.GetEnvironmentVariable("OM_TEST_MODE") == "1"
            && Environment.GetEnvironmentVariable("OM_TEST_AUTH_RATE_LIMIT_MODE") == "opt-in")
        {
            var header = http.Request.Headers["x-om-test-rate-limit"].ToString();
            return string.Equals(header, "on", StringComparison.Ordinal);
        }

        return ParseBool(Environment.GetEnvironmentVariable("RATE_LIMIT_ENABLED"), true);
    }

    private static string? ClientIp(HttpContext http)
    {
        var depth = ReadInt("RATE_LIMIT_TRUST_PROXY_DEPTH", 1);
        var forwarded = http.Request.Headers["x-forwarded-for"].ToString();
        if (!string.IsNullOrEmpty(forwarded) && depth > 0)
        {
            var ips = forwarded.Split(',').Select(x => x.Trim()).ToArray();
            var idx = ips.Length - depth;
            return idx >= 0 ? ips[idx] : ips[0];
        }
        var realIp = http.Request.Headers["x-real-ip"].ToString();
        return string.IsNullOrEmpty(realIp) ? null : realIp;
    }

    private static int ReadInt(string key, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(raw)) return fallback;
        return int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var t = raw.Trim().ToLowerInvariant();
        if (t is "1" or "true" or "yes" or "y" or "on" or "enable" or "enabled") return true;
        if (t is "0" or "false" or "no" or "n" or "off" or "disable" or "disabled") return false;
        return fallback;
    }
}
