using System.Collections.Concurrent;
using System.Text.Json;

using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace BuildingBlocks.Infrastructure.Caching;

/// <summary>
/// Cache-aside in Valkey, with two guards against a stampede when an entry is missing. Inside one
/// task, concurrent misses for a key share one load (single-flight). Across tasks, a short refill
/// lock in Valkey lets one task load while the others wait briefly for its answer. The shared load
/// runs on its own deadline, not the first caller's: a caller that gives up never takes the others
/// down with it. An invalidation deletes the entry twice, now and a moment later, so a load that
/// read the old row just before the change cannot keep it cached for the whole time to live.
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
        var (found, value) = await TryRead<T>(key);
        if (found)
        {
            return Result.Success(value!);
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
                var (found, value) = await TryRead<T>(key);
                if (found)
                {
                    return Result.Success(value!);
                }
            }

            return await load(cancellationToken);
        }

        try
        {
            var result = await load(cancellationToken);
            if (result.IsSuccess)
            {
                await TryWrite(key, JsonSerializer.Serialize(result.Value, Json), timeToLive);
            }

            return result;
        }
        finally
        {
            await TryRun(db => db.ScriptEvaluateAsync(ReleaseLock, [lockKey], [token]), lockKey);
        }
    }

    private async Task<(bool Found, T? Value)> TryRead<T>(string key)
    {
        try
        {
            var stored = await valkey.GetDatabase().StringGetAsync(key);
            return stored.IsNull ? (false, default) : (true, JsonSerializer.Deserialize<T>(stored.ToString(), Json));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnavailable(logger, key, e.Message);
            return (false, default);
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

    private Task TryWrite(string key, string json, TimeSpan timeToLive) =>
        TryRun(db => db.StringSetAsync(key, json, timeToLive), key);

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
