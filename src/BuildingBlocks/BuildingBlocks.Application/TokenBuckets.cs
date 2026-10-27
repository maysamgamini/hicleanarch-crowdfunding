namespace BuildingBlocks.Application;

/// <summary>A refill rate in tokens a second, and the most tokens a bucket can hold, which is the largest burst it allows.</summary>
public sealed record BucketRate(double PerSecond, int Capacity);

/// <summary>A bucket by name, with its rate. Every task that names the same bucket shares it.</summary>
public sealed record Bucket(string Name, BucketRate Rate);

/// <summary>The answer to a take: a token, or how long until one is likely to be free.</summary>
public readonly record struct TokenGrant(bool Granted, TimeSpan RetryAfter)
{
    public static TokenGrant Yes => new(true, TimeSpan.Zero);
}

/// <summary>
/// Token buckets that every task shares: a call takes one token from each bucket it names, all of
/// them or none, and a bucket refills at its rate. The pace is set by the buckets, so adding tasks
/// adds no calls.
/// </summary>
public interface ITokenBuckets
{
    Task<TokenGrant> TryTake(IReadOnlyList<Bucket> buckets, CancellationToken cancellationToken);

    /// <summary>
    /// Empties a bucket now: the provider said "too many" although the bucket had room, so every task
    /// waits a full refill before the next call.
    /// </summary>
    Task Drain(Bucket bucket, CancellationToken cancellationToken);
}
