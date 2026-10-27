using System.Globalization;

using BuildingBlocks.Application;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// Slots in Valkey, shared by every task: a sorted set of the slots held, each scored by the end of
/// its lease on Valkey's own clock, so a dead task's slots free themselves and tasks with different
/// clocks agree. One Lua script frees ended leases, checks the cap and takes a slot atomically.
/// While Valkey cannot be reached, a task uses its <see cref="DegradedInFlightSlots"/> share.
/// </summary>
public sealed partial class ValkeyInFlightSlots(IConnectionMultiplexer valkey, DegradedInFlightSlots degraded, ILogger<ValkeyInFlightSlots> logger) : IInFlightSlots
{
    // KEYS: the held slots (member = slot id, score = lease end in ms), the cap after a shrink (cap,
    // at_ms). ARGV: the pool's size, its lease in ms, the new slot's id, how soon to ask again in ms.
    // A shrunk cap grows back by one a second. Refused: the wait in ms until the first lease ends, or
    // until it is worth asking again, whichever is sooner.
    private const string Take = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local size = tonumber(ARGV[1])
        local cap = size
        local shrunk = redis.call('HMGET', KEYS[2], 'cap', 'at_ms')
        if shrunk[1] then
          cap = math.min(size, tonumber(shrunk[1]) + math.floor((now - tonumber(shrunk[2])) / 1000))
        end
        if redis.call('ZCARD', KEYS[1]) < cap then
          redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[3])
          redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]) + 1000)
          return {1, 0}
        end
        local wait = tonumber(ARGV[4])
        local first = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
        if first[2] then
          wait = math.max(1, math.min(wait, tonumber(first[2]) - now))
        end
        return {0, wait}
        """;

    // Halves the cap from what is allowed and held now, never below one; the key ends once the cap
    // would have grown back to the pool's size.
    private const string Halve = """
        local t = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        local size = tonumber(ARGV[1])
        local cap = size
        local shrunk = redis.call('HMGET', KEYS[2], 'cap', 'at_ms')
        if shrunk[1] then
          cap = math.min(size, tonumber(shrunk[1]) + math.floor((now - tonumber(shrunk[2])) / 1000))
        end
        local held = redis.call('ZCARD', KEYS[1])
        local halved = math.max(1, math.floor(math.min(cap, held) / 2))
        redis.call('HSET', KEYS[2], 'cap', tostring(halved), 'at_ms', tostring(now))
        redis.call('PEXPIRE', KEYS[2], (size - halved) * 1000 + 1000)
        return halved
        """;

    private const string LocalPrefix = "local:";

    private int _degraded;

    public async Task<SlotGrant> TryTake(SlotPool pool, CancellationToken cancellationToken)
    {
        if (!valkey.IsConnected)
        {
            return Local(await degraded.TryTake(pool, cancellationToken), null);
        }

        var id = Guid.NewGuid().ToString("N");
        try
        {
            var reply = (RedisResult[])(await valkey.GetDatabase().ScriptEvaluateAsync(Take, Keys(pool),
            [
                pool.Size.ToString(CultureInfo.InvariantCulture),
                ((long)pool.Lease.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                id,
                ((long)InProcessInFlightSlots.AskAgain.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            ]))!;
            if (Interlocked.Exchange(ref _degraded, 0) == 1)
            {
                LogRecovered(logger);
            }

            return (int)reply[0] == 1 ? new SlotGrant(id, TimeSpan.Zero) : new SlotGrant(null, TimeSpan.FromMilliseconds((long)reply[1]));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            return Local(await degraded.TryTake(pool, cancellationToken), e);
        }
    }

    public async Task Release(SlotPool pool, string slotId, CancellationToken cancellationToken)
    {
        if (slotId.StartsWith(LocalPrefix, StringComparison.Ordinal))
        {
            await degraded.Release(pool, slotId[LocalPrefix.Length..], cancellationToken);
            return;
        }

        try
        {
            await valkey.GetDatabase().SortedSetRemoveAsync(Keys(pool)[0], slotId);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Degrade(e); // the slot frees itself when its lease ends
        }
    }

    public async Task Shrink(SlotPool pool, CancellationToken cancellationToken)
    {
        await degraded.Shrink(pool, cancellationToken);
        if (!valkey.IsConnected)
        {
            return;
        }

        try
        {
            await valkey.GetDatabase().ScriptEvaluateAsync(Halve, Keys(pool), [pool.Size.ToString(CultureInfo.InvariantCulture)]);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            Degrade(e);
        }
    }

    private static RedisKey[] Keys(SlotPool pool) => [$"slots:{pool.Name}", $"slots:{pool.Name}:cap"];

    private SlotGrant Local(SlotGrant grant, Exception? exception)
    {
        Degrade(exception);
        return grant.Granted ? grant with { SlotId = LocalPrefix + grant.SlotId } : grant;
    }

    private void Degrade(Exception? exception)
    {
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            LogDegraded(logger, degraded.Tasks, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey cannot be reached: this task keeps its 1/{Tasks} share of the calls in flight in its own memory")]
    private static partial void LogDegraded(ILogger logger, int tasks, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Valkey is back: the shared in-flight slots are used again")]
    private static partial void LogRecovered(ILogger logger);
}
