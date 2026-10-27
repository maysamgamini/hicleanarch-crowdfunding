using Pledges.Application;
using Pledges.Contracts;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// The campaign page's totals, Round 2: what the aggregator last stored, a few seconds old at most.
/// A campaign it has not computed yet is computed now, from the ledger, and stored. The contract
/// other modules use does not change.
/// </summary>
internal sealed class CachedCampaignTotals(ITotalsStore store, ITotalsSource source, IFundingWindows windows) : ICampaignTotals
{
    public async Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken)
    {
        var totals = await store.Get(campaignId, cancellationToken);
        if (totals is null)
        {
            if (await windows.Find(campaignId, cancellationToken) is null)
            {
                return null;
            }

            totals = await source.Compute(campaignId, cancellationToken);
            await store.Set(campaignId, totals, cancellationToken);
        }

        return new CampaignTotals(totals.PledgedMinor, totals.Backers);
    }
}
