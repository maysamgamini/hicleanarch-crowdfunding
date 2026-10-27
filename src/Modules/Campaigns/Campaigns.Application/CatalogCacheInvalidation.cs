using BuildingBlocks.Application;

using Campaigns.Contracts;
using Campaigns.Domain;

using Payments.Contracts;

using Pledges.Contracts;

namespace Campaigns.Application;

/// <summary>
/// Drops a campaign's cached catalog when an event says its page changed. It runs through the
/// module's inbox like any handler; deleting an entry twice is harmless anyway. Every event that
/// changes what <see cref="GetCampaignCatalog"/> returns must be listed here.
/// </summary>
public sealed class CatalogCacheInvalidation(IQueryCache cache) :
    IIntegrationEventHandler<CampaignPublished>,
    IIntegrationEventHandler<CampaignCancelled>,
    IIntegrationEventHandler<FundingCancelled>,
    IIntegrationEventHandler<CampaignFundingDecided>,
    IIntegrationEventHandler<CampaignSettled>
{
    public Task Handle(CampaignPublished integrationEvent, CancellationToken cancellationToken) =>
        Drop(integrationEvent.CampaignId, cancellationToken);

    public Task Handle(CampaignCancelled integrationEvent, CancellationToken cancellationToken) =>
        Drop(integrationEvent.CampaignId, cancellationToken);

    public Task Handle(FundingCancelled integrationEvent, CancellationToken cancellationToken) =>
        Drop(integrationEvent.CampaignId, cancellationToken);

    public Task Handle(CampaignFundingDecided integrationEvent, CancellationToken cancellationToken) =>
        Drop(integrationEvent.CampaignId, cancellationToken);

    public Task Handle(CampaignSettled integrationEvent, CancellationToken cancellationToken) =>
        Drop(integrationEvent.CampaignId, cancellationToken);

    private Task Drop(Guid campaignId, CancellationToken cancellationToken) =>
        cache.Invalidate(GetCampaignCatalog.Key(new CampaignId(campaignId)), cancellationToken);
}
