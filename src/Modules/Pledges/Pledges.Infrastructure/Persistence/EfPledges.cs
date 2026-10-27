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

    public async Task<Contracts.CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken) =>
        await db.FundingWindows.AsNoTracking()
            .Where(w => w.CampaignId == campaignId)
            .Select(w => new Contracts.CampaignTotals(w.PledgedMinor, w.Backers))
            .SingleOrDefaultAsync(cancellationToken);
}
