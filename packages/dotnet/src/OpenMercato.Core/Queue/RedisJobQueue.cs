using System.Text.Json;
using StackExchange.Redis;

namespace OpenMercato.Core.Queue;

/// <summary>
/// Redis-backed IJobQueue. Layout per job:
///   INCR  om:queue:{q}:id            -> monotonically increasing job id
///   HSET  om:queue:{q}:jobs:{id}     name / data (JSON) / timestamp (unix ms)
///   LPUSH om:queue:{q}:wait          id
/// Workers move ids wait -> active (RPOPLPUSH) and mark completion on the hash.
/// </summary>
public sealed class RedisJobQueue : IJobQueue
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;

    public RedisJobQueue(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<string> EnqueueAsync(string queue, string jobName, object payload, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var id = (await db.StringIncrementAsync(QueueKeys.IdCounter(queue))).ToString();

        await db.HashSetAsync(QueueKeys.Job(queue, id), new HashEntry[]
        {
            new("name", jobName),
            new("data", JsonSerializer.Serialize(payload, JsonOptions)),
            new("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        });
        await db.ListLeftPushAsync(QueueKeys.Wait(queue), id);
        return id;
    }
}
