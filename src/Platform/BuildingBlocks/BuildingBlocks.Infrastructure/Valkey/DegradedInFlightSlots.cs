using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// The slots a task keeps in its own memory while Valkey cannot be reached: the pool's size divided
/// by the most tasks that run at once, so all of them together stay within the cap.
/// </summary>
public sealed class DegradedInFlightSlots(int tasks, TimeProvider clock) : IInFlightSlots
{
    private readonly InProcessInFlightSlots _local = new(clock);

    public int Tasks => tasks;

    public Task<SlotGrant> TryTake(SlotPool pool, CancellationToken cancellationToken) => _local.TryTake(Share(pool), cancellationToken);

    public Task Release(SlotPool pool, string slotId, CancellationToken cancellationToken) => _local.Release(Share(pool), slotId, cancellationToken);

    public Task Shrink(SlotPool pool, CancellationToken cancellationToken) => _local.Shrink(Share(pool), cancellationToken);

    private SlotPool Share(SlotPool pool) => pool with { Size = Math.Max(1, pool.Size / tasks) };
}
