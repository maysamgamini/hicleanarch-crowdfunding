using Campaigns.Domain;

namespace Campaigns.Application;

/// <summary>Where campaigns are kept. The application says what it needs; Infrastructure decides how.</summary>
public interface ICampaignRepository
{
    Task Add(Campaign campaign, CancellationToken cancellationToken);

    Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken);

    /// <summary>Moves the campaign from DRAFT to LIVE, only if it is still a draft. False: it no longer was.</summary>
    Task<bool> TryPublish(CampaignId id, DateTimeOffset publishedAt, CancellationToken cancellationToken);
}
