using Campaigns.Domain;

namespace Campaigns.Application;

/// <summary>Where campaigns are kept. The application says what it needs; Infrastructure decides how.</summary>
public interface ICampaignRepository
{
    Task Add(Campaign campaign, CancellationToken cancellationToken);

    Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken);
}
