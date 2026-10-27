using BuildingBlocks.Application;

namespace BuildingBlocks.Infrastructure.Valkey;

/// <summary>
/// The same buckets in this process's memory, for a laptop with no Valkey. Each task would have its
/// own, so the pace is right only while one task runs; every environment in AWS uses Valkey.
/// </summary>
public sealed class InProcessTokenBuckets(TimeProvider clock) : ITokenBuckets
{
    private readonly Dictionary<string, (double Tokens, DateTimeOffset At)> _buckets = [];
    private readonly Dictionary<string, (DateTimeOffset Due, DateTimeOffset Until)> _places = [];
    private readonly Lock _lock = new();

    public Task Drain(Bucket bucket, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _buckets[bucket.Name] = (0, clock.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    public Task<TokenGrant> TryTake(IReadOnlyList<Bucket> buckets, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            return Task.FromResult(Take(buckets, now));
        }
    }

    public Task<TokenGrant> TryEnter(Lane lane, string place, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var key = $"{lane.Turns.Name}:{place}";
        lock (_lock)
        {
            if (_places.TryGetValue(key, out var held) && held.Until > now)
            {
                if (held.Due > now)
                {
                    return Task.FromResult(new TokenGrant(false, held.Due - now));
                }

                var turn = Take(lane.AlsoTake, now);
                if (turn.Granted)
                {
                    _places.Remove(key);
                }

                return Task.FromResult(turn);
            }

            var level = Level(lane.Turns, now);
            if (level >= 1)
            {
                return Task.FromResult(Take([lane.Turns, .. lane.AlsoTake], now));
            }

            // A place in line: the lane's next token is taken now, so the level goes below zero by one
            // for every caller waiting, and this caller's token is due when it is back at zero.
            _buckets[lane.Turns.Name] = (level - 1, now);
            var due = now + TimeSpan.FromSeconds((1 - level) / lane.Turns.Rate.PerSecond);
            ForgetEndedPlaces(now);
            _places[key] = (due, now + lane.KeepPlaceFor);
            return Task.FromResult(new TokenGrant(false, due - now));
        }
    }

    private TokenGrant Take(IReadOnlyList<Bucket> buckets, DateTimeOffset now)
    {
        var levels = buckets.Select(b => Level(b, now)).ToArray();
        var wait = buckets.Select((b, i) => levels[i] >= 1 ? 0 : (1 - levels[i]) / b.Rate.PerSecond).DefaultIfEmpty(0).Max();
        var granted = wait == 0;
        for (var i = 0; i < buckets.Count; i++)
        {
            _buckets[buckets[i].Name] = (levels[i] - (granted ? 1 : 0), now);
        }

        return granted ? TokenGrant.Yes : new TokenGrant(false, TimeSpan.FromSeconds(wait));
    }

    private double Level(Bucket bucket, DateTimeOffset now)
    {
        var (tokens, at) = _buckets.GetValueOrDefault(bucket.Name, (bucket.Rate.Capacity, now));
        return Math.Min(bucket.Rate.Capacity, tokens + ((now - at).TotalSeconds * bucket.Rate.PerSecond));
    }

    private void ForgetEndedPlaces(DateTimeOffset now)
    {
        if (_places.Count < 10_000)
        {
            return;
        }

        foreach (var ended in _places.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
        {
            _places.Remove(ended);
        }
    }
}
