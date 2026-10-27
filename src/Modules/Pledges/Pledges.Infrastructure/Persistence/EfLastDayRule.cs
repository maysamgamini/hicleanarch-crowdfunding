using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Application;

namespace Pledges.Infrastructure.Persistence;

/// <summary>
/// The last-day rule with a transaction-scoped advisory lock per campaign, taken only by cancels
/// and decreases in the last 24 hours, then an exact sum of the ledger. The lock is released at
/// commit or rollback. A cancel runs as the request path's role, inside its two-second limit, so it
/// waits for the lock at most 300 ms and otherwise answers "try again".
/// </summary>
internal sealed class EfLastDayRule(PledgesDbContext db) : ILastDayRule
{
    public static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(300);

    public async Task<bool> Allows(Guid campaignId, long decreaseMinor, CancellationToken cancellationToken)
    {
        var onLastDay = await db.Database.SqlQuery<bool>($"""
            SELECT now() >= deadline - interval '24 hours' AS "Value" FROM pledges.funding_windows WHERE campaign_id = {campaignId}
            """).SingleAsync(cancellationToken);
        if (!onLastDay)
        {
            return true;
        }

        await DatabaseBusy.WithLockTimeout(sql => db.Database.ExecuteSqlRawAsync(sql, cancellationToken), LockWait, "try_again",
            () => db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT pg_advisory_xact_lock(hashtextextended('pledges.last_day:' || {campaignId}::text, 0))
                """, cancellationToken));

        // Read after the lock: every cancel that held it before has committed or rolled back.
        var window = await db.FundingWindows.AsNoTracking().Where(w => w.CampaignId == campaignId).Select(w => new { w.GoalMinor, w.OpenedAt }).SingleAsync(cancellationToken);
        var goal = window.GoalMinor;
        var sum = await db.Ledger.Where(e => e.CampaignId == campaignId && e.CreatedAt >= window.OpenedAt).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
        return sum < goal || sum - decreaseMinor >= goal;
    }
}
