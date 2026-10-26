using Campaigns.Application;
using Campaigns.Domain;

using Microsoft.EntityFrameworkCore;

namespace Campaigns.Infrastructure.Persistence;

internal sealed class EfCampaignRepository(CampaignsDbContext db) : ICampaignRepository
{
    public async Task Add(Campaign campaign, CancellationToken cancellationToken)
    {
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken) =>
        db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
}
