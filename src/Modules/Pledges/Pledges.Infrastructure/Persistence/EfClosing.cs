using Microsoft.EntityFrameworkCore;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

internal sealed class EfClosing(PledgesDbContext db) : IClosing
{
    // The partial index funding_windows_due holds exactly these rows.
    public async Task<IReadOnlyList<Guid>> Due(int batchSize, Guid? campaignId, CancellationToken cancellationToken)
    {
        var any = campaignId is null;
        var only = campaignId ?? Guid.Empty;
        return await db.Database.SqlQuery<Guid>($"""
            SELECT campaign_id AS "Value" FROM pledges.funding_windows
            WHERE status IN ('LIVE', 'ENDED') AND deadline <= now() AND ({any} OR campaign_id = {only})
            ORDER BY deadline LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
    }

    public async Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = 'ENDED', ended_at = now()
            WHERE campaign_id = {campaignId} AND status = 'LIVE' AND deadline <= now()
            """, cancellationToken) == 1;

    public async Task<(long SumMinor, int Backers, long GoalMinor, string Currency)> ExactSum(Guid campaignId, CancellationToken cancellationToken)
    {
        var window = await db.FundingWindows.AsNoTracking().SingleAsync(w => w.CampaignId == campaignId, cancellationToken);
        var sum = await db.Ledger.Where(e => e.CampaignId == campaignId).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
        var backers = await db.Pledges.CountAsync(p => p.CampaignId == campaignId && p.Status == Domain.PledgeStatus.Active, cancellationToken);
        return (sum, backers, window.GoalMinor, window.Currency);
    }

    public async Task<bool> TryDecide(Guid campaignId, bool funded, long finalMinor, CancellationToken cancellationToken)
    {
        var outcome = funded ? "FUNDED" : "UNSUCCESSFUL";
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = {outcome}, final_minor = {finalMinor}
            WHERE campaign_id = {campaignId} AND status = 'ENDED'
            """, cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ChargeablePledge>> MarkToCharge(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<ChargeablePledge>($"""
            UPDATE pledges.pledges SET status = 'TO_CHARGE'
            WHERE campaign_id = {campaignId} AND status = 'ACTIVE'
            RETURNING id AS pledge_id, backer_id, amount_minor, payment_method_ref
            """).ToListAsync(cancellationToken);

    public async Task<int> Release(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET status = 'RELEASED' WHERE campaign_id = {campaignId} AND status = 'ACTIVE'
            """, cancellationToken);

    public async Task<int> ExpireWaiting(Guid campaignId, CancellationToken cancellationToken)
    {
        var expired = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET status = 'EXPIRED' WHERE campaign_id = {campaignId} AND status = 'PENDING_SETUP'
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.tier_counters SET held = 0 WHERE campaign_id = {campaignId}
            """, cancellationToken);
        return expired;
    }
}
