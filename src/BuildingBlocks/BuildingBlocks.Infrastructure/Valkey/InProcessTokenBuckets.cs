using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// The same buckets in this process's memory, for a laptop with no Valkey. Each task would have its
/// own, so the pace is right only while one task runs; every environment in AWS uses Valkey.
/// </summary>
public sealed class InProcessTokenBuckets(TimeProvider clock) : ITokenBuckets
{
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = [];
    private readonly Lock _lock = new();

    public Task<TokenGrant> TryTake(IReadOnlyList<Bucket> buckets, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var levels = buckets.Select(b =>
            {
                var (tokens, at) = _buckets.GetValueOrDefault(b.Name, (b.Rate.Capacity, now));
                return Math.Min(b.Rate.Capacity, tokens + ((now - at).TotalSeconds * b.Rate.PerSecond));
            }).ToArray();
            var wait = buckets.Select((b, i) => levels[i] >= 1 ? 0 : (1 - levels[i]) / b.Rate.PerSecond).Max();
            var granted = wait == 0;
            for (var i = 0; i < buckets.Count; i++)
            {
                _buckets[buckets[i].Name] = (levels[i] - (granted ? 1 : 0), now);
            }

            return Task.FromResult(granted ? TokenGrant.Yes : new TokenGrant(false, TimeSpan.FromSeconds(wait)));
        }
    }
}
