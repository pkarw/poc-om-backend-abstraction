using StackExchange.Redis;

namespace OpenMercato.Core.Configuration;

/// <summary>
/// Central configuration. Env var names are IDENTICAL to upstream Open Mercato
/// (DATABASE_URL, REDIS_URL, QUEUE_STRATEGY, QUEUE_REDIS_URL, JWT_SECRET, PORT).
/// </summary>
public sealed record AppConfig(
    string DatabaseUrl,
    string RedisUrl,
    string QueueStrategy,
    string QueueRedisUrl,
    string JwtSecret,
    int Port)
{
    public static AppConfig FromEnvironment()
    {
        var redisUrl = Env("REDIS_URL", "redis://localhost:6379");
        return new AppConfig(
            DatabaseUrl: Env("DATABASE_URL", "postgres://mercato:mercato@localhost:5432/mercato"),
            RedisUrl: redisUrl,
            QueueStrategy: Env("QUEUE_STRATEGY", "redis"),
            QueueRedisUrl: Env("QUEUE_REDIS_URL", redisUrl),
            JwtSecret: Env("JWT_SECRET", "dev-secret-change-me"),
            Port: int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8080);
    }

    public string NpgsqlConnectionString => ConnectionStrings.FromDatabaseUrl(DatabaseUrl);

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

public static class ConnectionStrings
{
    /// <summary>
    /// Converts an upstream-style postgres://user:pass@host:port/db URL into an
    /// Npgsql keyword connection string. Keyword strings are passed through untouched.
    /// </summary>
    public static string FromDatabaseUrl(string url)
    {
        if (!url.Contains("://")) return url;

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var database = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;

        return $"Host={uri.Host};Port={port};Database={database};Username={user};Password={password}";
    }

    /// <summary>
    /// Converts a redis://[user:pass@]host[:port] URL into StackExchange.Redis options.
    /// AbortOnConnectFail=false so the host keeps retrying while Redis boots.
    /// </summary>
    public static ConfigurationOptions FromRedisUrl(string url)
    {
        var options = new ConfigurationOptions { AbortOnConnectFail = false };

        if (url.Contains("://"))
        {
            var uri = new Uri(url);
            options.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);
            if (uri.UserInfo.Length > 0)
            {
                var parts = uri.UserInfo.Split(':', 2);
                if (parts.Length == 2)
                {
                    if (parts[0].Length > 0) options.User = Uri.UnescapeDataString(parts[0]);
                    options.Password = Uri.UnescapeDataString(parts[1]);
                }
            }
        }
        else
        {
            options.EndPoints.Add(url);
        }

        return options;
    }
}
