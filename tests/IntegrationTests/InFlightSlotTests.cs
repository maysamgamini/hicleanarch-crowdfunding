using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Valkey;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Infrastructure;
using Payments.Infrastructure.Psp;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// The cap on PSP calls in flight beside the rate buckets (lesson s3-a04): in Valkey across tasks, and
/// in memory, every PSP call holds a slot while it runs, and a 429 for too many in progress shrinks it.
/// </summary>
public sealed class InFlightSlotTests(Valkey valkey) : IClassFixture<Valkey>, IDisposable
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

    /// <summary>The slots as some tasks have them: each its own connection to the shared Valkey, or one memory.</summary>
    private IInFlightSlots[] Tasks(string kind, int count)
    {
        if (kind == "memory")
        {
            var memory = new InProcessInFlightSlots(TimeProvider.System);
            return [.. Enumerable.Repeat<IInFlightSlots>(memory, count)];
        }

        return [.. Enumerable.Range(0, count).Select(_ =>
        {
            var connection = ConnectionMultiplexer.Connect(valkey.Configuration);
            _connections.Add(connection);
            return (IInFlightSlots)new ValkeyInFlightSlots(connection, new DegradedInFlightSlots(1, TimeProvider.System), NullLogger<ValkeyInFlightSlots>.Instance);
        })];
    }

    private static SlotPool NewPool(int size, TimeSpan lease) => new($"tests:{Guid.NewGuid():N}", size, lease);

    private static async Task<string> Take(IInFlightSlots slots, SlotPool pool)
    {
        while (true)
        {
            var slot = await slots.TryTake(pool, Ct);
            if (slot.SlotId is { } id)
            {
                return id;
            }

            await Task.Delay(slot.RetryAfter, Ct);
        }
    }

    [Theory]
    [InlineData("valkey")]
    [InlineData("memory")]
    public async Task Many_callers_on_several_tasks_never_hold_more_slots_than_the_cap(string kind)
    {
        var tasks = Tasks(kind, 3);
        var pool = NewPool(size: 5, lease: TimeSpan.FromMinutes(1));
        var inFlight = 0;
        var most = 0;

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            var slots = tasks[i % tasks.Length];
            var slot = await Take(slots, pool);
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            await Task.Delay(10, Ct);
            Interlocked.Decrement(ref inFlight);
            await slots.Release(pool, slot, Ct);
        }, Ct)));

        Assert.InRange(most, 1, 5);
    }

    [Theory]
    [InlineData("valkey")]
    [InlineData("memory")]
    public async Task A_slot_whose_holder_died_comes_back_when_its_lease_ends(string kind)
    {
        var slots = Tasks(kind, 1)[0];
        var pool = NewPool(size: 2, lease: TimeSpan.FromSeconds(1));
        Assert.True((await slots.TryTake(pool, Ct)).Granted); // its task dies without giving it back
        var started = DateTimeOffset.UtcNow;

        // A live caller keeps using the other slot, so the pool is never idle; the dead slot must come
        // back by its own lease, while both slots are asked for at once.
        var bothFree = false;
        while (!bothFree && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15))
        {
            var live = await Take(slots, pool);
            var other = await slots.TryTake(pool, Ct);
            bothFree = other.Granted;
            await slots.Release(pool, live, Ct);
            if (other.SlotId is { } id)
            {
                await slots.Release(pool, id, Ct);
            }

            await Task.Delay(100, Ct);
        }

        Assert.True(bothFree);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(500)); // not before the lease is nearly over
    }

    [Theory]
    [InlineData("valkey")]
    [InlineData("memory")]
    public async Task Too_many_in_progress_halves_the_cap_and_it_grows_back(string kind)
    {
        var slots = Tasks(kind, 1)[0];
        var pool = NewPool(size: 8, lease: TimeSpan.FromMinutes(1));
        var held = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            held.Add(await Take(slots, pool));
        }

        await slots.Shrink(pool, Ct);
        foreach (var slot in held)
        {
            await slots.Release(pool, slot, Ct);
        }

        var granted = 0;
        while ((await slots.TryTake(pool, Ct)).Granted && granted < 8)
        {
            granted++;
        }

        Assert.InRange(granted, 4, 7); // half of 8, plus a slot for each second the test took
        await Take(slots, pool).WaitAsync(TimeSpan.FromSeconds(30), Ct); // it grows back by a slot a second
    }

    [Fact]
    public async Task While_valkey_cannot_be_reached_a_task_keeps_its_share_of_the_cap()
    {
        using var down = await ConnectionMultiplexer.ConnectAsync(ValkeyRegistration.ClientOptions("127.0.0.1:1"));
        var slots = new ValkeyInFlightSlots(down, new DegradedInFlightSlots(4, TimeProvider.System), NullLogger<ValkeyInFlightSlots>.Instance);
        var pool = NewPool(size: 8, lease: TimeSpan.FromMinutes(1));

        var first = await slots.TryTake(pool, Ct);
        var second = await slots.TryTake(pool, Ct);
        var third = await slots.TryTake(pool, Ct);
        await slots.Release(pool, first.SlotId!, Ct);

        Assert.True(first.Granted);
        Assert.True(second.Granted);
        Assert.False(third.Granted);
        Assert.True((await slots.TryTake(pool, Ct)).Granted); // the slot given back is free again
    }

    [Fact]
    public async Task Every_psp_call_holds_a_slot_and_gives_it_back_whatever_its_end()
    {
        var (psp, budgeted, slots) = Budgeted();

        await budgeted.Charge(Charge(), Guid.NewGuid().ToString(), Ct);
        await budgeted.StartCardSetup("cus_test", Guid.NewGuid().ToString(), Ct);
        psp.Unreachable(true);
        await Assert.ThrowsAsync<HttpRequestException>(() => budgeted.Charge(Charge(), Guid.NewGuid().ToString(), Ct));

        Assert.Equal(3, slots.Taken);
        Assert.Equal(0, slots.Held);
        Assert.Equal(1, slots.Most);
    }

    [Fact]
    public async Task A_429_for_too_many_in_progress_shrinks_the_cap_and_the_call_is_sent_again()
    {
        var (psp, budgeted, slots) = Budgeted();
        psp.LimitConcurrencyNext(1);

        var outcome = await budgeted.Charge(Charge(), Guid.NewGuid().ToString(), Ct);

        Assert.IsType<ChargeOutcome.Succeeded>(outcome);
        Assert.Equal(1, slots.Shrinks);
        Assert.Equal(2, psp.Calls.Count(c => c.Call == "charge"));
        Assert.Equal(0, slots.Held);
    }

    private static ChargeRequest Charge() => new("cus_test", FakePaymentProvider.CardSucceeds, 1_000, "USD", $"plg_{Guid.NewGuid():N}", 1);

    private static (FakePaymentProvider Psp, BudgetedPaymentProvider Budgeted, CountingSlots Slots) Budgeted()
    {
        var psp = new FakePaymentProvider(Options.Create(new PaymentsOptions { WebhookSecret = Database.WebhookSecret }), TimeProvider.System);
        var slots = new CountingSlots(new InProcessInFlightSlots(TimeProvider.System));
        return (psp, new BudgetedPaymentProvider(psp, new InProcessTokenBuckets(TimeProvider.System), slots, new PspBudget { Prefix = $"tests:{Guid.NewGuid():N}" }, TimeProvider.System), slots);
    }

    private static void InterlockedMax(ref int most, int value)
    {
        var seen = Volatile.Read(ref most);
        while (value > seen)
        {
            var before = Interlocked.CompareExchange(ref most, value, seen);
            if (before == seen)
            {
                return;
            }

            seen = before;
        }
    }

    /// <summary>The slots, with what the test needs to see: how many were taken, are held, and the shrinks.</summary>
    private sealed class CountingSlots(IInFlightSlots inner) : IInFlightSlots
    {
        private int _taken;
        private int _held;
        private int _most;
        private int _shrinks;

        public int Taken => Volatile.Read(ref _taken);

        public int Held => Volatile.Read(ref _held);

        public int Most => Volatile.Read(ref _most);

        public int Shrinks => Volatile.Read(ref _shrinks);

        public async Task<SlotGrant> TryTake(SlotPool pool, CancellationToken cancellationToken)
        {
            var grant = await inner.TryTake(pool, cancellationToken);
            if (grant.Granted)
            {
                Interlocked.Increment(ref _taken);
                InterlockedMax(ref _most, Interlocked.Increment(ref _held));
            }

            return grant;
        }

        public Task Release(SlotPool pool, string slotId, CancellationToken cancellationToken)
        {
            Interlocked.Decrement(ref _held);
            return inner.Release(pool, slotId, cancellationToken);
        }

        public Task Shrink(SlotPool pool, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _shrinks);
            return inner.Shrink(pool, cancellationToken);
        }
    }
}
