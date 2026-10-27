using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

/// <summary>
/// The public totals: the funding window's status, deadline and final sum from the reader endpoint
/// (lesson s3-l02), and the total, backers and places left as the aggregator last stored them
/// (lesson s3-l05). A campaign it has not computed yet is computed now and stored.
/// </summary>
internal sealed class EfPublicTotals(ReadOnlyDb<PledgesDbContext> reader, ITotalsStore store, ITotalsSource source) : IPublicTotalsReads
{
    public async Task<FundingSnapshot?> Find(Guid campaignId, CancellationToken cancellationToken)
    {
        var window = await reader.Context.FundingWindows.AsNoTracking().SingleOrDefaultAsync(w => w.CampaignId == campaignId, cancellationToken);
        if (window is null)
        {
            return null;
        }

        var shown = await store.Get(campaignId, cancellationToken);
        if (shown is null)
        {
            shown = await source.Compute(campaignId, cancellationToken);
            await store.Set(campaignId, shown, cancellationToken);
        }

        var tiers = await reader.Context.TierCounters.AsNoTracking()
            .Where(t => t.CampaignId == campaignId)
            .OrderBy(t => t.TierId)
            .Select(t => new { t.TierId, t.LimitQty })
            .ToListAsync(cancellationToken);
        return new FundingSnapshot(window.CampaignId, window.Status, window.Deadline, window.GoalMinor, window.Currency,
            shown.PledgedMinor, shown.Backers, window.FinalMinor,
            [.. tiers.Select(t => new TierPlaces(t.TierId, t.LimitQty, t.LimitQty is null ? null : shown.TierRemaining.GetValueOrDefault(t.TierId, t.LimitQty.Value)))],
            shown.AsOf);
    }
}
