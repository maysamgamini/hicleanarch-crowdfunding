using System.Collections.Concurrent;

using Campaigns.Application;
using Campaigns.Domain;

namespace Campaigns.Infrastructure;

/// <summary>Keeps campaigns in this process's memory. Lesson s2-l02 replaces it with PostgreSQL.</summary>
internal sealed class InMemoryCampaignRepository : ICampaignRepository
{
    private readonly ConcurrentDictionary<CampaignId, Campaign> _campaigns = new();

    public Task Add(Campaign campaign, CancellationToken cancellationToken)
    {
        _campaigns[campaign.Id] = campaign;
        return Task.CompletedTask;
    }

    public Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken) =>
        Task.FromResult(_campaigns.GetValueOrDefault(id));
}
