using System.Collections.Concurrent;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// The campaigns whose display totals are out of date, in this task. A pledge's event marks its
/// campaign; the aggregator takes each one at most every <see cref="MinInterval"/>. Marking twice is
/// the same as marking once.
/// </summary>
internal sealed class DirtyCampaigns(TimeProvider clock)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, bool> _dirty = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastComputed = new();

    public void Mark(Guid campaignId) => _dirty[campaignId] = true;

    /// <summary>Dirty campaigns not computed in the last two seconds; they are no longer dirty once taken.</summary>
    public IReadOnlyList<Guid> TakeDue()
    {
        var now = clock.GetUtcNow();
        var due = new List<Guid>();
        foreach (var campaignId in _dirty.Keys)
        {
            if (_lastComputed.TryGetValue(campaignId, out var last) && now - last < MinInterval)
            {
                continue;
            }

            if (_dirty.TryRemove(campaignId, out _))
            {
                _lastComputed[campaignId] = now;
                due.Add(campaignId);
            }
        }

        return due;
    }
}
