using BuildingBlocks.Domain;
using BuildingBlocks.Infrastructure.Caching;

using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>The cache against a real Valkey: hits, misses, invalidation, and a stampede.</summary>
public sealed class QueryCacheTests(Valkey valkey) : IClassFixture<Valkey>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<ConnectionMultiplexer> _connections = [];

    public void Dispose()
    {
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }
    }

    /// <summary>A cache as one task has it: its own connection, its own in-process single-flight.</summary>
    private ValkeyQueryCache Task()
    {
        var connection = ConnectionMultiplexer.Connect(valkey.Configuration);
        _connections.Add(connection);
        return new ValkeyQueryCache(connection, TimeProvider.System, NullLogger<ValkeyQueryCache>.Instance);
    }

    private static string NewKey() => $"tests:{Guid.NewGuid():N}";

    /// <summary>A load that counts its calls and takes a while, like a query on a busy database.</summary>
    private sealed class CountingLoad(string value, TimeSpan delay)
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<Result<string>> Run(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await System.Threading.Tasks.Task.Delay(delay, ct);
            return Result.Success(value);
        }
    }

    [Fact]
    public async Task A_miss_loads_and_stores_and_the_next_read_is_a_hit()
    {
        var cache = Task();
        var key = NewKey();
        var load = new CountingLoad("v1", TimeSpan.Zero);

        var first = await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct);
        var second = await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct);

        Assert.Equal("v1", first.Value);
        Assert.Equal("v1", second.Value);
        Assert.Equal(1, load.Calls);
    }

    [Fact]
    public async Task An_entry_expires_after_its_time_to_live()
    {
        var cache = Task();
        var key = NewKey();
        var load = new CountingLoad("v1", TimeSpan.Zero);

        await cache.GetOrLoad(key, TimeSpan.FromMilliseconds(200), load.Run, Ct);
        await System.Threading.Tasks.Task.Delay(400, Ct);
        await cache.GetOrLoad(key, TimeSpan.FromMilliseconds(200), load.Run, Ct);

        Assert.Equal(2, load.Calls);
    }

    [Fact]
    public async Task An_invalidated_entry_is_loaded_again()
    {
        var cache = Task();
        var key = NewKey();
        await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), new CountingLoad("old", TimeSpan.Zero).Run, Ct);

        await cache.Invalidate(key, Ct);
        var fresh = await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), new CountingLoad("new", TimeSpan.Zero).Run, Ct);

        Assert.Equal("new", fresh.Value);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_take_the_others_down_with_it()
    {
        var cache = Task();
        var key = NewKey();
        var load = new CountingLoad("v1", TimeSpan.FromMilliseconds(300));
        using var impatient = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, impatient.Token);
        await System.Threading.Tasks.Task.Delay(50, Ct);
        var others = Enumerable.Range(0, 5).Select(_ => cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct)).ToList();
        await impatient.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.All(await System.Threading.Tasks.Task.WhenAll(others), r => Assert.Equal("v1", r.Value));
        Assert.Equal(1, load.Calls);
    }

    [Fact]
    public async Task A_stale_load_racing_an_invalidation_is_deleted_again()
    {
        var cache = Task();
        var key = NewKey();

        await cache.Invalidate(key, Ct);
        // A load that read the row before the change writes the old value just after the delete.
        await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), new CountingLoad("old", TimeSpan.Zero).Run, Ct);
        await System.Threading.Tasks.Task.Delay(ValkeyQueryCache.DeleteAgainAfter + TimeSpan.FromMilliseconds(500), Ct);
        var fresh = await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), new CountingLoad("new", TimeSpan.Zero).Run, Ct);

        Assert.Equal("new", fresh.Value);
    }

    [Fact]
    public async Task A_refusal_is_not_stored()
    {
        var cache = Task();
        var key = NewKey();
        var refusal = Error.NotFound("not_found", "No such thing.");

        await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), _ => System.Threading.Tasks.Task.FromResult(Result.Failure<string>(refusal)), Ct);
        var later = await cache.GetOrLoad(key, TimeSpan.FromMinutes(1), new CountingLoad("found", TimeSpan.Zero).Run, Ct);

        Assert.Equal("found", later.Value);
    }

    [Fact]
    public async Task A_hundred_concurrent_misses_in_one_task_load_once()
    {
        var cache = Task();
        var key = NewKey();
        var load = new CountingLoad("v1", TimeSpan.FromMilliseconds(200));
        using var start = new ManualResetEventSlim();

        var reads = Enumerable.Range(0, 100).Select(_ => System.Threading.Tasks.Task.Run(() =>
        {
            start.Wait(Ct);
            return cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct);
        }, Ct)).ToList();
        start.Set();
        var results = await System.Threading.Tasks.Task.WhenAll(reads);

        Assert.All(results, r => Assert.Equal("v1", r.Value));
        Assert.Equal(1, load.Calls);
    }

    [Fact]
    public async Task A_hundred_concurrent_misses_across_three_tasks_load_once()
    {
        ValkeyQueryCache[] tasks = [Task(), Task(), Task()];
        var key = NewKey();
        var load = new CountingLoad("v1", TimeSpan.FromMilliseconds(300));
        using var start = new ManualResetEventSlim();

        var reads = Enumerable.Range(0, 100).Select(i => System.Threading.Tasks.Task.Run(() =>
        {
            start.Wait(Ct);
            return tasks[i % 3].GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct);
        }, Ct)).ToList();
        start.Set();
        var results = await System.Threading.Tasks.Task.WhenAll(reads);

        Assert.All(results, r => Assert.Equal("v1", r.Value));
        Assert.Equal(1, load.Calls);
    }

    [Fact]
    public async Task A_stale_entry_is_served_at_once_while_one_caller_in_the_fleet_reloads_it_however_slow_the_load()
    {
        ValkeyQueryCache[] tasks = [Task(), Task(), Task()];
        var key = NewKey();
        var ttl = TimeSpan.FromSeconds(1);
        await tasks[0].GetOrLoad(key, ttl, new CountingLoad("v1", TimeSpan.Zero).Run, Ct);
        await System.Threading.Tasks.Task.Delay(ttl + TimeSpan.FromMilliseconds(200), Ct); // stale, still kept

        // The database is slow now: the reload takes longer than any task waits for another's refill.
        var reload = new CountingLoad("v2", ValkeyQueryCache.WaitForRefill + TimeSpan.FromMilliseconds(500));
        using var start = new ManualResetEventSlim();
        var reads = Enumerable.Range(0, 99).Select(i => System.Threading.Tasks.Task.Run(async () =>
        {
            start.Wait(Ct);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var read = await tasks[i % 3].GetOrLoad(key, ttl, reload.Run, Ct);
            return (read.Value, watch.Elapsed);
        }, Ct)).ToList();
        start.Set();
        var results = await System.Threading.Tasks.Task.WhenAll(reads);

        Assert.Equal(1, reload.Calls);
        Assert.Equal(98, results.Count(r => r.Value == "v1" && r.Elapsed < ValkeyQueryCache.WaitForRefill)); // served the old value at once
        Assert.Equal("v2", (await tasks[2].GetOrLoad(key, ttl, reload.Run, Ct)).Value);
    }

    [Fact]
    public async Task Concurrent_misses_in_one_task_load_once_even_while_another_task_holds_the_refill_lock()
    {
        var cache = Task();
        var key = NewKey();
        // "Another task" took the refill lock and never answers: every waiter here loads by itself after
        // WaitForRefill, unless the task's single-flight makes them share one load.
        using (var other = ConnectionMultiplexer.Connect(valkey.Configuration))
        {
            await other.GetDatabase().StringSetAsync(key + ":refill", "another-task", TimeSpan.FromSeconds(30));
        }

        var load = new CountingLoad("v1", TimeSpan.FromMilliseconds(300));
        using var start = new ManualResetEventSlim();
        var reads = Enumerable.Range(0, 50).Select(_ => System.Threading.Tasks.Task.Run(() =>
        {
            start.Wait(Ct);
            return cache.GetOrLoad(key, TimeSpan.FromMinutes(1), load.Run, Ct);
        }, Ct)).ToList();
        start.Set();
        var results = await System.Threading.Tasks.Task.WhenAll(reads);

        Assert.All(results, r => Assert.Equal("v1", r.Value));
        Assert.Equal(1, load.Calls);
    }

    [Fact]
    public async Task Without_valkey_every_read_loads_and_nothing_fails()
    {
        var connection = ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:1" },
            AbortOnConnectFail = false,
            ConnectTimeout = 100,
            SyncTimeout = 100,
            AsyncTimeout = 100,
        });
        _connections.Add(connection);
        var cache = new ValkeyQueryCache(connection, TimeProvider.System, NullLogger<ValkeyQueryCache>.Instance);
        var load = new CountingLoad("v1", TimeSpan.Zero);

        var first = await cache.GetOrLoad(NewKey(), TimeSpan.FromMinutes(1), load.Run, Ct);
        var second = await cache.GetOrLoad(NewKey(), TimeSpan.FromMinutes(1), load.Run, Ct);

        Assert.Equal("v1", first.Value);
        Assert.Equal("v1", second.Value);
        Assert.Equal(2, load.Calls);
    }
}
