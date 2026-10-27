using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Application;
using Campaigns.Domain;

using Microsoft.EntityFrameworkCore;

namespace Campaigns.Infrastructure.Persistence;

internal sealed class EfCampaignReads(ReadOnlyDb<CampaignsDbContext> reader) : ICampaignReads
{
    public Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken) =>
        reader.Context.Campaigns.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
}
