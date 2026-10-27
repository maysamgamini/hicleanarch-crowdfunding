using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;

namespace Pledges.Infrastructure.Persistence;

internal sealed class EfPledges(PledgesDbContext db) : IPledges, IPledgeLedger, ITierSlots, Contracts.ICampaignTotals
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
