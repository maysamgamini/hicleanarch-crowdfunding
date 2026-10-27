using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// The display totals from the ledger's sum, the pledges that count, and each limited tier's places
/// left, read on the read side (the Aurora reader): every two seconds per hot campaign and every live
/// campaign once a minute, these sums stay off the one writer. The reader's lag, usually well under a
/// second, adds to the shown total's age; the exact sum that decides a campaign is the close's, on the writer.
/// </summary>
internal sealed class EfTotalsSource(ReadOnlyDb<PledgesDbContext> read, TimeProvider clock) : ITotalsSource
{
    private PledgesDbContext Db => read.Context;

    private static readonly PledgeStatus[] Counted = [PledgeStatus.Active, PledgeStatus.ToCharge, PledgeStatus.Charged];

    public async Task<DisplayTotals?> Compute(Guid campaignId, CancellationToken cancellationToken)
    {
        var asOf = clock.GetUtcNow();
        var window = await Db.FundingWindows.AsNoTracking().Where(w => w.CampaignId == campaignId).Select(w => new { w.OpenedAt }).SingleOrDefaultAsync(cancellationToken);
        if (window is null)
        {
            return null;
        }

        var opened = window.OpenedAt;
        var sum = await Db.Ledger.Where(e => e.CampaignId == campaignId && e.CreatedAt >= opened).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
        var backers = await Db.Pledges.CountAsync(p => p.CampaignId == campaignId && Counted.Contains(p.Status), cancellationToken);
        var tiers = await Db.TierCounters.AsNoTracking()
            .Where(t => t.CampaignId == campaignId && t.LimitQty != null)
            .Select(t => new { t.TierId, Left = t.LimitQty!.Value - t.Claimed - t.Held })
            .ToDictionaryAsync(t => t.TierId, t => t.Left, cancellationToken);
        return new DisplayTotals(sum, backers, tiers, asOf);
    }

    public async Task<IReadOnlyList<Guid>> Live(CancellationToken cancellationToken) =>
        await Db.FundingWindows.AsNoTracking()
            .Where(w => w.Status == FundingStatus.Live)
            .Select(w => w.CampaignId)
            .ToListAsync(cancellationToken);
}
