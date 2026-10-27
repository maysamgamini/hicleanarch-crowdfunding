using System.Collections.Concurrent;

using StackExchange.Redis;

namespace CrowdFunding.Gateway;

/// <summary>The answer for one request: let it through, or refuse it and say when to try again.</summary>
public readonly record struct BucketDecision(bool Allowed, TimeSpan RetryAfter);

public interface ITokenBuckets
{
    Task<BucketDecision> Take(string client);
}

/// <summary>
/// One token bucket per client in Valkey, refilled by the clock of Valkey itself, so every gateway task
/// sees the same bucket and no task's clock matters. One script call per request.
/// </summary>
internal sealed class ValkeyTokenBuckets(IConnectionMultiplexer valkey, EdgeLimitOptions options) : ITokenBuckets
{
    // KEYS[1] the client's bucket; ARGV[1] tokens per second; ARGV[2] burst.
    // Returns {1, 0} when a token was taken, {0, ms to wait} when the bucket is empty.
    private const string Script = """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local rate = tonumber(ARGV[1])
        local burst = tonumber(ARGV[2])
        local b = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
        local tokens = tonumber(b[1]) or burst
        local ts = tonumber(b[2]) or now
        tokens = math.min(burst, tokens + (now - ts) * rate / 1000)
        local allowed, wait = 0, 0
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
        else
            wait = math.ceil((1 - tokens) * 1000 / rate)
        end
        redis.call('HSET', KEYS[1], 'tokens', tostring(tokens), 'ts', now)
        redis.call('PEXPIRE', KEYS[1], math.ceil(burst * 1000 / rate) + 1000)
        return {allowed, wait}
        """;

    /// <summary>False while the connection is down or reconnecting: callers limit locally instead of waiting.</summary>
    public bool IsConnected => valkey.IsConnected;

    public async Task<BucketDecision> Take(string client)
    {
        var result = (RedisResult[])(await valkey.GetDatabase().ScriptEvaluateAsync(
            Script,
            [new RedisKey($"gateway:bucket:{client}")],
            [options.PerSecond, options.Burst]))!;
        return new BucketDecision((int)result[0] == 1, TimeSpan.FromMilliseconds((int)result[1]));
    }
}

/// <summary>
/// The same bucket kept in this process's memory. Used when Valkey cannot be reached, at the limit
/// divided by the number of gateway tasks, so the whole fleet still stays near the limit.
/// </summary>
internal sealed class LocalTokenBuckets(EdgeLimitOptions options, TimeProvider clock) : ITokenBuckets
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    private double Rate => options.PerSecond / Math.Max(1, options.GatewayTasks);

    private double Size => Math.Max(1.0, (double)options.Burst / Math.Max(1, options.GatewayTasks));

    public Task<BucketDecision> Take(string client)
    {
        var bucket = _buckets.GetOrAdd(client, _ => new Bucket(Size, clock.GetTimestamp()));
        lock (bucket)
        {
            var now = clock.GetTimestamp();
            bucket.Tokens = Math.Min(Size, bucket.Tokens + (clock.GetElapsedTime(bucket.Stamp, now).TotalSeconds * Rate));
            bucket.Stamp = now;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                return Task.FromResult(new BucketDecision(true, TimeSpan.Zero));
            }

            return Task.FromResult(new BucketDecision(false, TimeSpan.FromSeconds((1 - bucket.Tokens) / Rate)));
        }
    }

    private sealed class Bucket(double tokens, long stamp)
    {
        public double Tokens { get; set; } = tokens;

        public long Stamp { get; set; } = stamp;
    }
}
