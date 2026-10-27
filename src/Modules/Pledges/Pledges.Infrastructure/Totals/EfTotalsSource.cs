using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// The display totals from the source of truth: the ledger's sum, the pledges that count, and each
/// limited tier's places left. Lesson s3-l02's reader connection is where this belongs once it exists.
/// </summary>
internal sealed class EfTotalsSource(PledgesDbContext db, TimeProvider clock) : ITotalsSource
{
    private static readonly PledgeStatus[] Counted = [PledgeStatus.Active, PledgeStatus.ToCharge, PledgeStatus.Charged];

    public async Task<DisplayTotals> Compute(Guid campaignId, CancellationToken cancellationToken)
    {
        var asOf = clock.GetUtcNow();
        var sum = await db.Ledger.Where(e => e.CampaignId == campaignId).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
        var backers = await db.Pledges.CountAsync(p => p.CampaignId == campaignId && Counted.Contains(p.Status), cancellationToken);
        var tiers = await db.TierCounters.AsNoTracking()
            .Where(t => t.CampaignId == campaignId && t.LimitQty != null)
            .Select(t => new { t.TierId, Left = t.LimitQty!.Value - t.Claimed - t.Held })
            .ToDictionaryAsync(t => t.TierId, t => t.Left, cancellationToken);
        return new DisplayTotals(sum, backers, tiers, asOf);
    }

    public async Task<IReadOnlyList<Guid>> Live(CancellationToken cancellationToken) =>
        await db.FundingWindows.AsNoTracking()
            .Where(w => w.Status == FundingStatus.Live)
            .Select(w => w.CampaignId)
            .ToListAsync(cancellationToken);
}
