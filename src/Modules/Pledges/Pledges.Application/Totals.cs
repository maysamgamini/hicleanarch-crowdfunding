namespace Pledges.Application;

/// <summary>
/// What a page shows for a campaign: the total and backers as last recomputed from the ledger, the
/// places left in each limited tier, and when that was. Never used to decide anything.
/// </summary>
public sealed record DisplayTotals(long PledgedMinor, int Backers, IReadOnlyDictionary<Guid, int> TierRemaining, DateTimeOffset AsOf);

/// <summary>
/// Where the aggregator keeps display totals. It always writes the absolute value, so writing the
/// same campaign twice, or late, never adds anything up twice.
/// </summary>
public interface ITotalsStore
{
    Task Set(Guid campaignId, DisplayTotals totals, CancellationToken cancellationToken);

    /// <summary>Null when the aggregator has not computed this campaign yet.</summary>
    Task<DisplayTotals?> Get(Guid campaignId, CancellationToken cancellationToken);
}

/// <summary>Recomputes a campaign's display totals from the ledger, the pledges and the tier counters.</summary>
public interface ITotalsSource
{
    Task<DisplayTotals> Compute(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Campaigns still taking pledges, for the aggregator's periodic drift check.</summary>
    Task<IReadOnlyList<Guid>> Live(CancellationToken cancellationToken);
}
