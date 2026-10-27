namespace BuildingBlocks.Application;

/// <summary>
/// A cap on calls in flight, by name: at most <see cref="Size"/> at once across every task, each
/// slot held for at most <see cref="Lease"/>. The lease is longer than any call's own timeout, so
/// only a task that died loses its slots to it.
/// </summary>
public sealed record SlotPool(string Name, int Size, TimeSpan Lease);

/// <summary>A slot to give back when the call ends, or how long until one is likely to be free.</summary>
public readonly record struct SlotGrant(string? SlotId, TimeSpan RetryAfter)
{
    public bool Granted => SlotId is not null;
}

/// <summary>
/// Calls in flight that every task shares (lesson s3-a04): a call takes a slot before it is sent and
/// gives it back when it ends, success or failure. A slot whose task died is free again when its
/// lease runs out. A provider that says its concurrency limit was hit shrinks the pool, which grows
/// back by a slot a second.
/// </summary>
public interface IInFlightSlots
{
    Task<SlotGrant> TryTake(SlotPool pool, CancellationToken cancellationToken);

    Task Release(SlotPool pool, string slotId, CancellationToken cancellationToken);

    /// <summary>Halves the calls allowed in flight now: the provider refused one for its concurrency limit.</summary>
    Task Shrink(SlotPool pool, CancellationToken cancellationToken);
}
