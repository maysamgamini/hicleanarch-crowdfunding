using System.Diagnostics.Metrics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

/// <summary>The time fence's numbers: the request path's transaction limit and the close's margin over it.</summary>
public sealed record TimeFence(TimeSpan TransactionLimit, TimeSpan WaitAfterEnd)
{
    public static readonly TimeFence Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
}

internal sealed partial class EfClosing(PledgesDbContext db, TimeFence fence, ILogger<EfClosing> logger) : IClosing
{
    private static readonly Meter Meter = new("CrowdFunding.Pledges");

    // Alarmed on in s2-l16's dashboard: any value above zero means a ledger entry landed after a decision's sum.
    private static readonly Counter<long> Mismatches = Meter.CreateCounter<long>("pledges.close.recheck_mismatches");

    public TimeSpan WaitAfterEnd => fence.WaitAfterEnd;

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

    // ended_at is the statement's own clock, not the transaction's start, so it is as close to the
    // ENDED commit as the database can say.
    public async Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = 'ENDED', ended_at = clock_timestamp()
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
        // Never sooner than the fence's wait after the close: a pass that comes too early decides
        // nothing, and the next pass decides it.
        var wait = fence.WaitAfterEnd;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = {outcome}, final_minor = {finalMinor}, decided_at = now()
            WHERE campaign_id = {campaignId} AND status = 'ENDED' AND ended_at <= now() - {wait}
            """, cancellationToken) == 1;
    }

    public async Task<int> RecheckDecided(int batchSize, CancellationToken cancellationToken)
    {
        // The sum again, a minute after the decision, kept beside the recorded one. Later entries of
        // other kinds (a dropped charge) come long after, so a minute later only a fence failure differs.
        var rechecked = await db.Database.SqlQuery<Recheck>($"""
            UPDATE pledges.funding_windows w
            SET rechecked_at = now(),
                recheck_minor = (SELECT coalesce(sum(delta_minor), 0) FROM pledges.pledge_ledger l
                                 WHERE l.campaign_id = w.campaign_id AND l.created_at <= w.decided_at + interval '1 minute')
            WHERE w.campaign_id IN (SELECT campaign_id FROM pledges.funding_windows
                                    WHERE decided_at <= now() - interval '1 minute' AND rechecked_at IS NULL
                                    ORDER BY decided_at LIMIT {batchSize})
            RETURNING w.campaign_id, w.final_minor, w.recheck_minor
            """).ToListAsync(cancellationToken);
        var differed = rechecked.Where(r => r.FinalMinor != r.RecheckMinor).ToList();
        foreach (var r in differed)
        {
            LogMismatch(logger, r.CampaignId, r.FinalMinor, r.RecheckMinor);
            Mismatches.Add(1);
        }

        return differed.Count;
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Campaign {CampaignId} was decided on {FinalMinor}, but its ledger sums to {RecheckMinor}: an entry landed after the fence")]
    private static partial void LogMismatch(ILogger logger, Guid campaignId, long finalMinor, long recheckMinor);

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

/// <summary>One campaign's recorded sum beside its sum a minute later.</summary>
internal sealed record Recheck(Guid CampaignId, long FinalMinor, long RecheckMinor);
