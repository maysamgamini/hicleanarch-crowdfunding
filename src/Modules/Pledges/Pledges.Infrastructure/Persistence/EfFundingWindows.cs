using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;

namespace Pledges.Infrastructure.Persistence;

internal sealed class EfFundingWindows(PledgesDbContext db) : IFundingWindows
{
    public async Task<bool> TryOpen(FundingWindow window, IReadOnlyList<TierCounter> tiers, CancellationToken cancellationToken)
    {
        // A second CampaignPublished for the same campaign inserts nothing.
        var opened = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pledges.funding_windows (campaign_id, status, deadline, goal_minor, currency, pledged_minor, backers)
            VALUES ({window.CampaignId}, 'LIVE', {window.Deadline}, {window.GoalMinor}, {window.Currency}, 0, 0)
            ON CONFLICT (campaign_id) DO NOTHING
            """, cancellationToken);
        foreach (var tier in tiers)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO pledges.tier_counters (tier_id, campaign_id, min_pledge_minor, limit_qty, claimed, held)
                VALUES ({tier.TierId}, {tier.CampaignId}, {tier.MinPledgeMinor}, {tier.LimitQty}, 0, 0)
                ON CONFLICT (tier_id) DO NOTHING
                """, cancellationToken);
        }

        return opened == 1;
    }

    public async Task<CancelOutcome> TryCancel(Guid campaignId, CancellationToken cancellationToken)
    {
        // The close's own condition: still LIVE, and before the deadline by the database's clock.
        var cancelled = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = 'CANCELLED'
            WHERE campaign_id = {campaignId} AND status = 'LIVE' AND now() < deadline
            """, cancellationToken);
        if (cancelled == 1)
        {
            return CancelOutcome.Cancelled;
        }

        var status = await db.FundingWindows.AsNoTracking()
            .Where(w => w.CampaignId == campaignId)
            .Select(w => (FundingStatus?)w.Status)
            .SingleOrDefaultAsync(cancellationToken);
        return status switch
        {
            null => CancelOutcome.NotOpen,
            FundingStatus.Cancelled => CancelOutcome.AlreadyCancelled,
            _ => CancelOutcome.Closed,
        };
    }
}
