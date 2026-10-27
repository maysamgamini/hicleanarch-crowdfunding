using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// The same slots in this process's memory, for a laptop with no Valkey and for a task's share while
/// Valkey cannot be reached. Same rule as <see cref="ValkeyInFlightSlots"/>: leases end, a shrink
/// halves the cap, and it grows back by a slot a second.
/// </summary>
public sealed class InProcessInFlightSlots(TimeProvider clock) : IInFlightSlots
{
    /// <summary>How soon a caller that found no slot asks again: a slot is usually given back long before a lease ends.</summary>
    internal static readonly TimeSpan AskAgain = TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<string, Pool> _pools = [];
    private readonly Lock _lock = new();

    public Task<SlotGrant> TryTake(SlotPool pool, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var state = Live(pool, now);
            if (state.Held.Count >= state.Cap(pool, now))
            {
                var untilFirstEnds = state.Held.Values.DefaultIfEmpty(now + AskAgain).Min() - now;
                var wait = untilFirstEnds < AskAgain ? TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, untilFirstEnds.Ticks)) : AskAgain;
                return Task.FromResult(new SlotGrant(null, wait));
            }

            var id = Guid.NewGuid().ToString("N");
            state.Held[id] = now + pool.Lease;
            return Task.FromResult(new SlotGrant(id, TimeSpan.Zero));
        }
    }

    public Task Release(SlotPool pool, string slotId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_pools.TryGetValue(pool.Name, out var state))
            {
                state.Held.Remove(slotId);
            }
        }

        return Task.CompletedTask;
    }

    public Task Shrink(SlotPool pool, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var state = Live(pool, now);
            state.ShrunkTo = Math.Max(1, Math.Min(state.Cap(pool, now), state.Held.Count) / 2);
            state.ShrunkAt = now;
        }

        return Task.CompletedTask;
    }

    private Pool Live(SlotPool pool, DateTimeOffset now)
    {
        if (!_pools.TryGetValue(pool.Name, out var state))
        {
            _pools[pool.Name] = state = new Pool();
        }

        foreach (var ended in state.Held.Where(h => h.Value <= now).Select(h => h.Key).ToArray())
        {
            state.Held.Remove(ended);
        }

        return state;
    }

    private sealed class Pool
    {
        public Dictionary<string, DateTimeOffset> Held { get; } = [];

        public int? ShrunkTo { get; set; }

        public DateTimeOffset ShrunkAt { get; set; }

        public int Cap(SlotPool pool, DateTimeOffset now) =>
            ShrunkTo is { } shrunk ? (int)Math.Min(pool.Size, shrunk + Math.Floor((now - ShrunkAt).TotalSeconds)) : pool.Size;
    }
}
