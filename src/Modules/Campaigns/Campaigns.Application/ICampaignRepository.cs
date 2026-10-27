using Campaigns.Domain;

namespace Campaigns.Application;

/// <summary>Where campaigns are kept. The application says what it needs; Infrastructure decides how.</summary>
public interface ICampaignRepository
{
    Task Add(Campaign campaign, CancellationToken cancellationToken);

    Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken);

    /// <summary>Moves the campaign from DRAFT to LIVE, only if it is still a draft. False: it no longer was.</summary>
    Task<bool> TryPublish(CampaignId id, DateTimeOffset publishedAt, CancellationToken cancellationToken);

    /// <summary>Moves the campaign from <paramref name="from"/> to CANCELLED, only if it is still there.</summary>
    Task<bool> TryCancel(CampaignId id, CampaignStatus from, CancellationToken cancellationToken);

    /// <summary>Moves the status people see from <paramref name="from"/> to <paramref name="to"/>, only if it is still there.</summary>
    Task<bool> TryMove(CampaignId id, CampaignStatus from, CampaignStatus to, CancellationToken cancellationToken);

    /// <summary>Moves the status people see to <paramref name="to"/> from any of <paramref name="from"/>, in one conditional update.</summary>
    Task<bool> TryMove(CampaignId id, IReadOnlyCollection<CampaignStatus> from, CampaignStatus to, CancellationToken cancellationToken);
}
