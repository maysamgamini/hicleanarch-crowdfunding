using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

/// <summary>The public totals, read from the reader endpoint, like every page (lesson s3-l02).</summary>
internal sealed class EfPublicTotals(ReadOnlyDb<PledgesDbContext> reader) : IPublicTotalsReads
{
    public async Task<FundingSnapshot?> Find(Guid campaignId, CancellationToken cancellationToken)
    {
        var window = await reader.Context.FundingWindows.AsNoTracking().SingleOrDefaultAsync(w => w.CampaignId == campaignId, cancellationToken);
        if (window is null)
        {
            return null;
        }

        var tiers = await reader.Context.TierCounters.AsNoTracking()
            .Where(t => t.CampaignId == campaignId)
            .OrderBy(t => t.TierId)
            .Select(t => new TierPlaces(t.TierId, t.LimitQty, t.Claimed, t.Held))
            .ToListAsync(cancellationToken);
        return new FundingSnapshot(window.CampaignId, window.Status, window.Deadline, window.GoalMinor, window.Currency,
            window.PledgedMinor, window.Backers, window.FinalMinor, tiers);
    }
}
