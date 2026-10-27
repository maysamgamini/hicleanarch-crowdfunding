using System.Collections.Concurrent;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Caching;

/// <summary>
/// Cache-aside in Valkey, with guards against a stampede. An entry is fresh for its time to live and
/// kept as long again: once it is stale, one caller in the whole fleet (the holder of a short refill
/// lock in Valkey) loads it again while every other caller gets the old value at once
/// (stale-while-revalidate), so a slow database still sees one query per key. When an entry is
/// missing, concurrent misses in one task share one load (single-flight), and other tasks wait
/// briefly for the lock holder's answer. The shared load runs on its own deadline, not the first
/// caller's. An invalidation deletes the entry twice, now and a moment later, so a changed status is
/// never served stale and a load that read the old row just before the change cannot keep it.
/// </summary>
public sealed partial class ValkeyQueryCache(IConnectionMultiplexer valkey, TimeProvider clock, ILogger<ValkeyQueryCache> logger) : IQueryCache
{
    /// <summary>How long a refill may take before another task may try.</summary>
    public static readonly TimeSpan RefillLock = TimeSpan.FromSeconds(5);

    /// <summary>How long a task waits for another task's refill before it loads by itself.</summary>
    public static readonly TimeSpan WaitForRefill = TimeSpan.FromSeconds(1);

    /// <summary>When the second delete of an invalidation runs.</summary>
    public static readonly TimeSpan DeleteAgainAfter = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Releases the refill lock only if this task still holds it, so a slow task never frees another's lock.
    private const string ReleaseLock = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inFlight = new();

    public async Task<Result<T>> GetOrLoad<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load, CancellationToken cancellationToken)
    {
        var entry = await TryRead<T>(key);
        if (entry is { Fresh: true })
        {
            return Result.Success(entry.Value!);
        }

        if (entry is { } stale)
        {
            return await Revalidate(key, timeToLive, load, stale.Value!, cancellationToken);
        }

        var flight = _inFlight.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await SharedRefill(key, timeToLive, load)));
        try
        {
            return (Result<T>)await flight.Value.WaitAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && flight.Value.IsCompleted)
        {
            // The shared load failed, perhaps because the caller whose services it used went away:
            // this caller loads for itself.
            return await load(cancellationToken);
        }
        finally
        {
            _inFlight.TryRemove(KeyValuePair.Create(key, flight));
        }
    }

    private async Task<Result<T>> SharedRefill<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load)
    {
        using var deadline = new CancellationTokenSource(RefillLock, clock);
        return await Refill(key, timeToLive, load, deadline.Token);
    }

    /// <summary>
    /// A stale entry: this caller loads it again only if no one else is (in this task, or holding the
    /// refill lock in another); every other caller, and this one if the load fails, gets the old value.
    /// </summary>
    private async Task<Result<T>> Revalidate<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load, T stale, CancellationToken cancellationToken)
    {
        var mine = new Lazy<Task<object>>(() => Task.FromResult<object>(Result.Success(stale)));
        if (!_inFlight.TryAdd(key, mine))
        {
            return Result.Success(stale);
        }

        try
        {
            var token = Guid.NewGuid().ToString("N");
            var lockKey = key + ":refill";
            if (!await TryTakeLock(lockKey, token))
            {
                return Result.Success(stale);
            }

            try
            {
                var result = await load(cancellationToken);
                if (!result.IsSuccess)
                {
                    return result;
                }

                await TryWrite(key, result.Value!, timeToLive);
                return result;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return Result.Success(stale); // the database is failing: the old value is better than none
            }
            finally
            {
                await TryRun(db => db.ScriptEvaluateAsync(ReleaseLock, [lockKey], [token]), lockKey);
            }
        }
        finally
        {
            _inFlight.TryRemove(KeyValuePair.Create(key, mine));
        }
    }

    public async Task Invalidate(string key, CancellationToken cancellationToken)
    {
        await TryRun(db => db.KeyDeleteAsync(key), key);
        _ = DeleteAgain(key);
    }

    private async Task DeleteAgain(string key)
    {
        await Task.Delay(DeleteAgainAfter, clock);
        await TryRun(db => db.KeyDeleteAsync(key), key); // if this fails too, the entry expires on its own
    }

    private async Task<Result<T>> Refill<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var lockKey = key + ":refill";
        if (!await TryTakeLock(lockKey, token))
        {
            // Another task is loading this key: use its answer when it arrives.
            for (var waited = TimeSpan.Zero; waited < WaitForRefill; waited += Poll)
            {
                await Task.Delay(Poll, clock, cancellationToken);
                if (await TryRead<T>(key) is { } entry)
                {
                    return Result.Success(entry.Value!);
                }
            }

            return await load(cancellationToken);
        }

        try
        {
            var result = await load(cancellationToken);
            if (result.IsSuccess)
            {
                await TryWrite(key, result.Value!, timeToLive);
            }

            return result;
        }
        finally
        {
            await TryRun(db => db.ScriptEvaluateAsync(ReleaseLock, [lockKey], [token]), lockKey);
        }
    }

    /// <summary>A stored value and whether it is still fresh.</summary>
    private sealed record Entry<T>(T? Value, bool Fresh);

    // Stored as "<fresh until, unix ms>|<json>". A value in another form (written before entries had
    // a freshness, during a rolling deploy) counts as missing.
    private async Task<Entry<T>?> TryRead<T>(string key)
    {
        try
        {
            var stored = await valkey.GetDatabase().StringGetAsync(key);
            if (stored.IsNull)
            {
                return null;
            }

            var text = stored.ToString();
            var bar = text.IndexOf('|', StringComparison.Ordinal);
            if (bar <= 0 || !long.TryParse(text.AsSpan(0, bar), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var freshUntil))
            {
                return null;
            }

            return new Entry<T>(JsonSerializer.Deserialize<T>(text.AsSpan(bar + 1), Json), clock.GetUtcNow().ToUnixTimeMilliseconds() < freshUntil);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnavailable(logger, key, e.Message);
            return null;
        }
    }

    /// <summary>When Valkey cannot be reached, every task loads for itself: slower, never wrong.</summary>
    private async Task<bool> TryTakeLock(string lockKey, string token)
    {
        try
        {
            return await valkey.GetDatabase().StringSetAsync(lockKey, token, RefillLock, When.NotExists);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnavailable(logger, lockKey, e.Message);
            return true;
        }
    }

    /// <summary>Fresh for the time to live, then kept as long again to be served while one caller reloads it.</summary>
    private Task TryWrite<T>(string key, T value, TimeSpan timeToLive)
    {
        var freshUntil = (clock.GetUtcNow() + timeToLive).ToUnixTimeMilliseconds();
        var stored = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{freshUntil}|{JsonSerializer.Serialize(value, Json)}");
        return TryRun(db => db.StringSetAsync(key, stored, timeToLive * 2), key);
    }

    private async Task TryRun(Func<IDatabase, Task> action, string key)
    {
        try
        {
            await action(valkey.GetDatabase());
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnavailable(logger, key, e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey unavailable for {Key}; reading from the database: {Error}")]
    private static partial void LogUnavailable(ILogger logger, string key, string error);
}

/// <summary>No cache configured: every query reaches its handler. Behaviour is the same, only slower.</summary>
internal sealed class NoQueryCache : IQueryCache
{
    public Task<Result<T>> GetOrLoad<T>(string key, TimeSpan timeToLive, Func<CancellationToken, Task<Result<T>>> load, CancellationToken cancellationToken) =>
        load(cancellationToken);

    public Task Invalidate(string key, CancellationToken cancellationToken) => Task.CompletedTask;
}
