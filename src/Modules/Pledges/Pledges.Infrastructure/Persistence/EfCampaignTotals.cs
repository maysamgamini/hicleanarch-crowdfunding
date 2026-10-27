using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Contracts;

namespace Pledges.Infrastructure.Persistence;

/// <summary>Totals for pages, read from the reader endpoint. Decisions sum the ledger on the writer.</summary>
internal sealed class EfCampaignTotals(ReadOnlyDb<PledgesDbContext> reader) : ICampaignTotals
{
    public async Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken) =>
        await reader.Context.FundingWindows
            .Where(w => w.CampaignId == campaignId)
            .Select(w => new CampaignTotals(w.PledgedMinor, w.Backers))
            .SingleOrDefaultAsync(cancellationToken);
}
