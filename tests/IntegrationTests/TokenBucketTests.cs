using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>The shared token buckets against a real Valkey: a burst, a refill, all or nothing, and several tasks.</summary>
public sealed class TokenBucketTests(Valkey valkey) : IClassFixture<Valkey>, IDisposable
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

    /// <summary>The buckets as one task has them: its own connection to the shared Valkey.</summary>
    private ValkeyTokenBuckets Task()
    {
        var connection = ConnectionMultiplexer.Connect(valkey.Configuration);
        _connections.Add(connection);
        return new ValkeyTokenBuckets(connection, NullLogger<ValkeyTokenBuckets>.Instance);
    }

    private static Bucket NewBucket(double perSecond, int capacity) => new($"tests:{Guid.NewGuid():N}", new BucketRate(perSecond, capacity));

    [Fact]
    public async Task A_full_bucket_gives_its_capacity_at_once_then_says_how_long_to_wait()
    {
        var buckets = Task();
        var bucket = NewBucket(perSecond: 2, capacity: 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await buckets.TryTake([bucket], Ct)).Granted);
        }

        var refused = await buckets.TryTake([bucket], Ct);
        Assert.False(refused.Granted);
        Assert.InRange(refused.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(500)); // one token every 500 ms
    }

    [Fact]
    public async Task An_empty_bucket_refills_at_its_rate()
    {
        var buckets = Task();
        // One token a second: the refusal must not be undone by a slow machine between the two takes.
        var bucket = NewBucket(perSecond: 1, capacity: 1);
        Assert.True((await buckets.TryTake([bucket], Ct)).Granted);
        var refused = await buckets.TryTake([bucket], Ct);
        Assert.False(refused.Granted);

        await System.Threading.Tasks.Task.Delay(refused.RetryAfter + TimeSpan.FromMilliseconds(50), Ct);

        Assert.True((await buckets.TryTake([bucket], Ct)).Granted);
    }

    [Fact]
    public async Task A_take_is_all_or_nothing_across_the_buckets_it_names()
    {
        var buckets = Task();
        // Rates far too slow to add a token while the test runs, however loaded the machine: this test
        // is about all or nothing, not refill.
        var account = NewBucket(perSecond: 0.0001, capacity: 3);
        var setups = NewBucket(perSecond: 0.0001, capacity: 1);
        Assert.True((await buckets.TryTake([account, setups], Ct)).Granted);

        Assert.False((await buckets.TryTake([account, setups], Ct)).Granted); // the lane is empty

        // The refused take left the account's tokens alone: two are still there.
        Assert.True((await buckets.TryTake([account], Ct)).Granted);
        Assert.True((await buckets.TryTake([account], Ct)).Granted);
        Assert.False((await buckets.TryTake([account], Ct)).Granted);
    }

    [Fact]
    public async Task Three_tasks_together_never_take_more_than_the_bucket_allows()
    {
        var tasks = new[] { Task(), Task(), Task() };
        var bucket = NewBucket(perSecond: 20, capacity: 10);
        var started = DateTimeOffset.UtcNow;
        var granted = 0;

        await System.Threading.Tasks.Task.WhenAll(tasks.Select(t => System.Threading.Tasks.Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                if ((await t.TryTake([bucket], Ct)).Granted)
                {
                    Interlocked.Increment(ref granted);
                }
            }
        }, Ct)));

        var allowed = 10 + ((DateTimeOffset.UtcNow - started).TotalSeconds * 20) + 1;
        Assert.InRange(granted, 10, (int)allowed);
    }

    [Fact]
    public async Task Without_Valkey_the_buckets_live_in_the_process_and_keep_the_same_rule()
    {
        var buckets = new InProcessTokenBuckets(TimeProvider.System);
        var bucket = NewBucket(perSecond: 0.0001, capacity: 2); // no refill while the test runs

        Assert.True((await buckets.TryTake([bucket], Ct)).Granted);
        Assert.True((await buckets.TryTake([bucket], Ct)).Granted);
        var refused = await buckets.TryTake([bucket], Ct);

        Assert.False(refused.Granted);
        Assert.True(refused.RetryAfter > TimeSpan.Zero);
    }
}
