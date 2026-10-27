using System.Globalization;

using BuildingBlocks.Application;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// Token buckets in Valkey, shared by every task. One Lua script refills and takes atomically across
/// all the buckets a call names, by Valkey's own clock, so tasks with different clocks agree. While
/// Valkey cannot be reached, the task takes from its <see cref="DegradedTokenBuckets"/>, its share of
/// each limit, so calls go on at a safe pace instead of stopping; a lost connection is used again as
/// soon as it comes back.
/// </summary>
public sealed partial class ValkeyTokenBuckets(IConnectionMultiplexer valkey, DegradedTokenBuckets degraded, ILogger<ValkeyTokenBuckets> logger) : ITokenBuckets
{
    private int _degraded;

    // KEYS: one hash per bucket (tokens, at_ms). ARGV: per bucket, its rate a second and its capacity.
    // Every bucket is refilled for the time since it was last touched; if each has a whole token, one
    // is taken from each; otherwise none is, and the reply is the wait in milliseconds for the
    // emptiest one. A bucket left alone expires once it would be full again.
    private const string Take = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        local tokens = {}
        local wait = 0
        for i, key in ipairs(KEYS) do
          local rate = tonumber(ARGV[2 * i - 1])
          local capacity = tonumber(ARGV[2 * i])
          local state = redis.call('HMGET', key, 'tokens', 'at_ms')
          local level = tonumber(state[1]) or capacity
          local at = tonumber(state[2]) or now
          level = math.min(capacity, level + (now - at) * rate / 1000)
          tokens[i] = level
          if level < 1 then
            wait = math.max(wait, math.ceil((1 - level) * 1000 / rate))
          end
        end
        local granted = 0
        if wait == 0 then granted = 1 end
        for i, key in ipairs(KEYS) do
          local rate = tonumber(ARGV[2 * i - 1])
          local capacity = tonumber(ARGV[2 * i])
          local level = tokens[i] - granted
          redis.call('HSET', key, 'tokens', tostring(level), 'at_ms', tostring(now))
          redis.call('PEXPIRE', key, math.ceil((capacity - level) * 1000 / rate) + 1000)
        end
        return {granted, wait}
        """;

    private const string Empty = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        redis.call('HSET', KEYS[1], 'tokens', '0', 'at_ms', tostring(now))
        redis.call('PEXPIRE', KEYS[1], math.ceil(tonumber(ARGV[1]) * 1000 / tonumber(ARGV[2])) + 1000)
        return 1
        """;

    public async Task Drain(Bucket bucket, CancellationToken cancellationToken)
    {
        await degraded.Drain(bucket, cancellationToken); // the share slows down too, should Valkey go next
        if (!valkey.IsConnected)
        {
            return;
        }

        try
        {
            await valkey.GetDatabase().ScriptEvaluateAsync(Empty, [(RedisKey)$"buckets:{bucket.Name}"],
            [
                bucket.Rate.Capacity.ToString(CultureInfo.InvariantCulture),
                bucket.Rate.PerSecond.ToString(CultureInfo.InvariantCulture),
            ]);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Degrade(e);
        }
    }

    public async Task<TokenGrant> TryTake(IReadOnlyList<Bucket> buckets, CancellationToken cancellationToken)
    {
        var keys = buckets.Select(b => (RedisKey)$"buckets:{b.Name}").ToArray();
        var args = buckets.SelectMany(b => new RedisValue[]
        {
            b.Rate.PerSecond.ToString(CultureInfo.InvariantCulture),
            b.Rate.Capacity.ToString(CultureInfo.InvariantCulture),
        }).ToArray();
        // A connection that is down answers from the share at once, not after a timeout on every call.
        if (!valkey.IsConnected)
        {
            Degrade(null);
            return await degraded.TryTake(buckets, cancellationToken);
        }

        try
        {
            var reply = (RedisResult[])(await valkey.GetDatabase().ScriptEvaluateAsync(Take, keys, args))!;
            if (Interlocked.Exchange(ref _degraded, 0) == 1)
            {
                LogRecovered(logger);
            }

            return (int)reply[0] == 1 ? TokenGrant.Yes : new TokenGrant(false, TimeSpan.FromMilliseconds((long)reply[1]));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Degrade(e);
            return await degraded.TryTake(buckets, cancellationToken);
        }
    }

    private void Degrade(Exception? exception)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            LogDegraded(logger, degraded.Tasks, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey cannot be reached: this task takes its 1/{Tasks} share of every limit from its own memory")]
    private static partial void LogDegraded(ILogger logger, int tasks, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Valkey is back: the shared buckets are used again")]
    private static partial void LogRecovered(ILogger logger);
}
