using BuildingBlocks.Application;

using Pledges.Contracts;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// The aggregator's signal: Pledges' own events, written to its outbox in the pledge's transaction
/// and handed over after the commit. A repeated or late event only causes one more recompute.
/// </summary>
internal sealed class MarkCampaignDirty(DirtyCampaigns dirty) :
    IIntegrationEventHandler<PledgeActivated>,
    IIntegrationEventHandler<PledgeChanged>,
    IIntegrationEventHandler<PledgeCancelled>
{
    public Task Handle(PledgeActivated activated, CancellationToken cancellationToken) => Mark(activated.CampaignId);

    public Task Handle(PledgeChanged changed, CancellationToken cancellationToken) => Mark(changed.CampaignId);

    public Task Handle(PledgeCancelled cancelled, CancellationToken cancellationToken) => Mark(cancelled.CampaignId);

    private Task Mark(Guid campaignId)
    {
        dirty.Mark(campaignId);
        return Task.CompletedTask;
    }
}
