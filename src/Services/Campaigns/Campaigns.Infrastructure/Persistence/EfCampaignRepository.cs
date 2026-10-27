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

    // UPDATE campaigns.campaigns SET status = 'LIVE', published_at = @now WHERE id = @id AND status = 'DRAFT'
    public async Task<bool> TryPublish(CampaignId id, DateTimeOffset publishedAt, CancellationToken cancellationToken) =>
        await db.Campaigns
            .Where(c => c.Id == id && c.Status == CampaignStatus.Draft)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.Status, CampaignStatus.Live)
                .SetProperty(c => c.PublishedAt, publishedAt), cancellationToken) == 1;

    public Task<bool> TryCancel(CampaignId id, CampaignStatus from, CancellationToken cancellationToken) =>
        TryMove(id, from, CampaignStatus.Cancelled, cancellationToken);

    public async Task<bool> TryMove(CampaignId id, CampaignStatus from, CampaignStatus to, CancellationToken cancellationToken) =>
        await db.Campaigns
            .Where(c => c.Id == id && c.Status == from)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, to), cancellationToken) == 1;

    public async Task<bool> TryMove(CampaignId id, IReadOnlyCollection<CampaignStatus> from, CampaignStatus to, CancellationToken cancellationToken) =>
        await db.Campaigns
            .Where(c => c.Id == id && from.Contains(c.Status))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, to), cancellationToken) == 1;
}
