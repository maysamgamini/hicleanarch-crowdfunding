using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// The buckets a task keeps in its own memory while Valkey cannot be reached: every bucket's rate
/// and burst divided by the most tasks that run at once, so all of them together stay within the
/// limit, as the gateway's degraded mode does (lesson s3-a06). With fewer tasks running, the pace is
/// lower than the limit, never higher. PSP calls go on at that share instead of stopping.
/// </summary>
public sealed class DegradedTokenBuckets(int tasks, TimeProvider clock) : ITokenBuckets
{
    /// <summary>Used when <c>Valkey:Tasks</c> is not set, as the gateway's own default.</summary>
    public const int DefaultTasks = 2;

    private readonly InProcessTokenBuckets _local = new(clock);

    public int Tasks => tasks;

    public Task<TokenGrant> TryTake(IReadOnlyList<Bucket> buckets, CancellationToken cancellationToken) =>
        _local.TryTake([.. buckets.Select(Share)], cancellationToken);

    public Task Drain(Bucket bucket, CancellationToken cancellationToken) => _local.Drain(Share(bucket), cancellationToken);

    private Bucket Share(Bucket bucket) =>
        bucket with { Rate = new BucketRate(bucket.Rate.PerSecond / tasks, Math.Max(1, bucket.Rate.Capacity / tasks)) };
}
