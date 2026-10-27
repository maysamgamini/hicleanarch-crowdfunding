using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;

namespace Pledges.Infrastructure.Persistence;

internal sealed class EfPledges(PledgesDbContext db) : IPledges, IPledgeLedger, ITierSlots, IHolds, Contracts.ICampaignTotals
{
    public async Task<bool> TryAdd(Pledge pledge, CancellationToken cancellationToken)
    {
        // The partial unique index decides. ON CONFLICT keeps the transaction usable when it says no;
        // a concurrent insert for the same backer is waited for, then counted as a conflict.
        var status = StatusNames.Of(pledge.Status);
        var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pledges.pledges (id, campaign_id, backer_id, tier_id, amount_minor, status, payment_method_ref, hold_expires_at)
            VALUES ({pledge.Id}, {pledge.CampaignId}, {pledge.BackerId}, {pledge.TierId}, {pledge.AmountMinor}, {status},
                    {pledge.PaymentMethodRef}, {pledge.HoldExpiresAt})
            ON CONFLICT (campaign_id, backer_id) WHERE status IN ('PENDING_SETUP', 'ACTIVE') DO NOTHING
            """, cancellationToken);
        return added == 1;
    }

    public Task<Pledge?> Find(Guid pledgeId, CancellationToken cancellationToken) =>
        db.Pledges.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pledgeId, cancellationToken);

    public async Task<Pledge?> FindForChange(Guid pledgeId, CancellationToken cancellationToken) =>
        (await db.Pledges.FromSql($"SELECT * FROM pledges.pledges WHERE id = {pledgeId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken))
        .SingleOrDefault();

    public async Task<bool> TrySaveFromActive(Pledge pledge, CancellationToken cancellationToken)
    {
        var status = StatusNames.Of(pledge.Status);
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET amount_minor = {pledge.AmountMinor}, tier_id = {pledge.TierId}, status = {status}
            WHERE id = {pledge.Id} AND status = 'ACTIVE'
            """, cancellationToken) == 1;
    }

    public async Task<bool> TryActivate(Guid pledgeId, string paymentMethodRef, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET status = 'ACTIVE', payment_method_ref = {paymentMethodRef}, hold_expires_at = NULL
            WHERE id = {pledgeId} AND status = 'PENDING_SETUP' AND hold_expires_at > now()
            """, cancellationToken) == 1;

    public async Task<bool> TryRecordCharge(Guid pledgeId, bool charged, CancellationToken cancellationToken)
    {
        var status = charged ? "CHARGED" : "FAILED";
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET status = {status} WHERE id = {pledgeId} AND status = 'TO_CHARGE'
            """, cancellationToken) == 1;
    }

    public async Task<bool> TryExpireActivated(Guid pledgeId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.pledges SET status = 'EXPIRED' WHERE id = {pledgeId} AND status = 'ACTIVE'
            """, cancellationToken) == 1;

    public async Task<int> ExpireDue(int batchSize, CancellationToken cancellationToken)
    {
        var due = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM pledges.pledges
            WHERE status = 'PENDING_SETUP' AND hold_expires_at <= now()
            ORDER BY hold_expires_at LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var id in due)
        {
            // One short transaction per pledge, conditional on it still waiting: a card that arrives
            // at the same moment either activates it first, or finds it EXPIRED.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tier = await db.Database.SqlQuery<Guid?>($"""
                UPDATE pledges.pledges SET status = 'EXPIRED'
                WHERE id = {id} AND status = 'PENDING_SETUP' AND hold_expires_at <= now()
                RETURNING tier_id AS "Value"
                """).ToListAsync(cancellationToken);
            if (tier.Count == 1)
            {
                expired++;
                if (tier[0] is { } tierId)
                {
                    await ReleaseHold(tierId, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return expired;
    }

    public async Task Append(LedgerEntry entry, CancellationToken cancellationToken)
    {
        db.Ledger.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    Task<TierCounter?> ITierSlots.Find(Guid tierId, CancellationToken cancellationToken) =>
        db.TierCounters.AsNoTracking().SingleOrDefaultAsync(t => t.TierId == tierId, cancellationToken);

    // Lesson 6: the rule is in the WHERE. A second transaction waits for the row lock, then re-checks
    // the row as the first left it (Read Committed); 0 rows changed means sold out.
    public async Task<bool> TryClaim(Guid tierId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.tier_counters SET claimed = claimed + 1
            WHERE tier_id = {tierId} AND limit_qty IS NOT NULL AND claimed + held + 1 <= limit_qty
            """, cancellationToken) == 1;

    public async Task<bool> TryHold(Guid tierId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.tier_counters SET held = held + 1
            WHERE tier_id = {tierId} AND limit_qty IS NOT NULL AND claimed + held + 1 <= limit_qty
            """, cancellationToken) == 1;

    public Task HoldToClaim(Guid tierId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.tier_counters SET held = held - 1, claimed = claimed + 1
            WHERE tier_id = {tierId} AND limit_qty IS NOT NULL
            """, cancellationToken);

    public Task ReleaseHold(Guid tierId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE pledges.tier_counters SET held = held - 1 WHERE tier_id = {tierId} AND limit_qty IS NOT NULL
            """, cancellationToken);

    public async Task<bool> TryMove(Guid? fromTierId, Guid? toTierId, CancellationToken cancellationToken)
    {
        if (fromTierId == toTierId)
        {
            return true;
        }

        Guid[] ids = [.. new[] { fromTierId, toTierId }.OfType<Guid>().Order()];
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM pledges.tier_counters WHERE tier_id = ANY({ids}) ORDER BY tier_id FOR UPDATE
            """, cancellationToken);
        if (fromTierId is { } from)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE pledges.tier_counters SET claimed = claimed - 1 WHERE tier_id = {from} AND limit_qty IS NOT NULL
                """, cancellationToken);
        }

        if (toTierId is not { } to)
        {
            return true;
        }

        var unlimited = await db.TierCounters.AsNoTracking().AnyAsync(t => t.TierId == to && t.LimitQty == null, cancellationToken);
        return unlimited || await TryClaim(to, cancellationToken);
    }

    public async Task<Contracts.CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken) =>
        await db.FundingWindows.AsNoTracking()
            .Where(w => w.CampaignId == campaignId)
            .Select(w => new Contracts.CampaignTotals(w.PledgedMinor, w.Backers))
            .SingleOrDefaultAsync(cancellationToken);
}
