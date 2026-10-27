using System.Diagnostics.Metrics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

/// <summary>The time fence's numbers: the request path's transaction limit and the close's margin over it.</summary>
public sealed record TimeFence(TimeSpan TransactionLimit, TimeSpan WaitAfterEnd, TimeSpan TierWait)
{
    public static readonly TimeFence Default = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300));
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
            WHERE ((status IN ('LIVE', 'ENDED') AND deadline <= now()) OR (status = 'FUNDED' AND marking_finished_at IS NULL))
              AND ({any} OR campaign_id = {only})
            ORDER BY deadline LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
    }

    // ended_at is the statement's own clock, not the transaction's start, so it is as close to the
    // ENDED commit as the database can say.
    public async Task<TimeSpan> StillToWait(IReadOnlyList<Guid> campaignIds, CancellationToken cancellationToken)
    {
        var ids = campaignIds.ToArray();
        var wait = fence.WaitAfterEnd;
        var seconds = await db.Database.SqlQuery<double>($"""
            SELECT coalesce(max(extract(epoch FROM (ended_at + {wait} - clock_timestamp()))), 0)::float8 AS "Value"
            FROM pledges.funding_windows WHERE campaign_id = ANY({ids}) AND status = 'ENDED'
            """).SingleAsync(cancellationToken);
        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    public async Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET status = 'ENDED', ended_at = clock_timestamp()
            WHERE campaign_id = {campaignId} AND status = 'LIVE' AND deadline <= now()
            """, cancellationToken) == 1;

    public async Task<(long SumMinor, int Backers, long GoalMinor, string Currency)> ExactSum(Guid campaignId, CancellationToken cancellationToken)
    {
        var window = await db.FundingWindows.AsNoTracking().SingleAsync(w => w.CampaignId == campaignId, cancellationToken);
        // Bounded by the window's opening, so the partitioned ledger reads only this campaign's months (lesson s3-l10).
        var sum = await db.Ledger.Where(e => e.CampaignId == campaignId && e.CreatedAt >= window.OpenedAt).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
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
            UPDATE pledges.funding_windows
            SET status = {outcome}, final_minor = {finalMinor}, decided_at = now(),
                marking_finished_at = CASE WHEN {funded} THEN NULL ELSE now() END
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
                                 WHERE l.campaign_id = w.campaign_id AND l.created_at >= w.opened_at AND l.created_at <= w.decided_at + interval '1 minute')
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

    public async Task<IReadOnlyList<ChargeablePledge>> MarkNextBatch(Guid campaignId, int batchSize, CancellationToken cancellationToken)
    {
        // The window's row is locked first, so two passes mark one batch after the other, each from
        // the cursor the other left.
        var window = await db.Database.SqlQuery<MarkingCursor>($"""
            SELECT marked_through, currency FROM pledges.funding_windows
            WHERE campaign_id = {campaignId} AND status = 'FUNDED' AND marking_finished_at IS NULL
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (window.Count == 0)
        {
            return [];
        }

        // The nil uuid sorts before every other, so a marking that has not started begins at the first pledge.
        var after = window[0].MarkedThrough ?? Guid.Empty;
        var marked = await db.Database.SqlQuery<ChargeablePledge>($"""
            WITH next AS (
                SELECT id FROM pledges.pledges
                WHERE campaign_id = {campaignId} AND status = 'ACTIVE' AND id > {after}
                ORDER BY id
                LIMIT {batchSize})
            UPDATE pledges.pledges p SET status = 'TO_CHARGE'
            FROM next WHERE p.id = next.id
            RETURNING p.id AS pledge_id, p.backer_id, p.amount_minor, p.payment_method_ref, {window[0].Currency} AS currency
            """).ToListAsync(cancellationToken);
        // PostgreSQL orders uuids by their bytes, as the 32-digit text spells them.
        var through = marked.Count > 0 ? marked.Select(p => p.PledgeId).MaxBy(id => id.ToString("N"), StringComparer.Ordinal) : after;
        var finished = marked.Count < batchSize;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows
            SET marked_through = {through}, marking_finished_at = CASE WHEN {finished} THEN now() ELSE NULL END
            WHERE campaign_id = {campaignId}
            """, cancellationToken);
        return marked;
    }

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

    // marking_finished_at says, for a cancelled window as for an unsuccessful one, that its pledges are released.
    public async Task<IReadOnlyList<Guid>> CancelledToRelease(int batchSize, Guid? campaignId, CancellationToken cancellationToken)
    {
        var any = campaignId is null;
        var only = campaignId ?? Guid.Empty;
        return await db.Database.SqlQuery<Guid>($"""
            SELECT campaign_id AS "Value" FROM pledges.funding_windows
            WHERE status = 'CANCELLED' AND marking_finished_at IS NULL AND ({any} OR campaign_id = {only})
            ORDER BY deadline LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
    }

    // Each waiting pledge gives back the place it held, as the sweeper's expiry does; the sweeper and
    // this batch both move only PENDING_SETUP rows, so a place is given back once. SKIP LOCKED: a
    // pledge a change or an activation holds is left for the next batch, never waited for.
    public async Task<IReadOnlyList<ReleasedPledge>> ReleaseNextBatch(Guid campaignId, int batchSize, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<ReleasedPledge>($"""
            WITH next AS (
                SELECT id FROM pledges.pledges
                WHERE campaign_id = {campaignId} AND status IN ('ACTIVE', 'PENDING_SETUP')
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED),
            moved AS (
                UPDATE pledges.pledges p SET status = CASE WHEN p.status = 'ACTIVE' THEN 'RELEASED' ELSE 'EXPIRED' END
                FROM next WHERE p.id = next.id
                RETURNING p.id, p.backer_id, p.tier_id, p.status),
            given_back AS (
                UPDATE pledges.tier_counters t SET held = t.held - waiting.places
                FROM (SELECT tier_id, count(*)::int AS places FROM moved WHERE status = 'EXPIRED' AND tier_id IS NOT NULL GROUP BY tier_id) waiting
                WHERE t.tier_id = waiting.tier_id AND t.limit_qty IS NOT NULL)
            SELECT id AS pledge_id, backer_id FROM moved
            """).ToListAsync(cancellationToken);

    public async Task<bool> TryFinishRelease(Guid campaignId, CancellationToken cancellationToken)
    {
        var wait = fence.WaitAfterEnd;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.funding_windows SET marking_finished_at = now()
            WHERE campaign_id = {campaignId} AND status = 'CANCELLED' AND marking_finished_at IS NULL
              AND coalesce(ended_at, '-infinity') <= now() - {wait}
              AND NOT EXISTS (SELECT 1 FROM pledges.pledges WHERE campaign_id = {campaignId} AND status IN ('ACTIVE', 'PENDING_SETUP'))
            """, cancellationToken) == 1;
    }
}

/// <summary>One campaign's recorded sum beside its sum a minute later.</summary>
internal sealed record Recheck(Guid CampaignId, long FinalMinor, long RecheckMinor);

/// <summary>Where a funded campaign's marking stands, and the currency its charges are in.</summary>
internal sealed record MarkingCursor(Guid? MarkedThrough, string Currency);
