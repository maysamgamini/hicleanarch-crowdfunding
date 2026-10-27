using BuildingBlocks.Application;

using Campaigns.Domain;

using Pledges.Contracts;

namespace Campaigns.Application;

/// <summary>
/// The status people see follows Pledges' decision. The move is conditional on LIVE, so a repeat of
/// the event, or one about a campaign already moved, changes nothing.
/// </summary>
public sealed class FollowFundingDecision(ICampaignRepository campaigns) : IIntegrationEventHandler<CampaignFundingDecided>
{
    public Task Handle(CampaignFundingDecided decided, CancellationToken cancellationToken) =>
        campaigns.TryMove(
            new CampaignId(decided.CampaignId),
            CampaignStatus.Live,
            decided.Outcome == CampaignFundingDecided.Funded ? CampaignStatus.Funded : CampaignStatus.Unsuccessful,
            cancellationToken);
}

/// <summary>Every charge of a funded campaign has ended: FUNDED to SETTLED.</summary>
public sealed class FollowSettlement(ICampaignRepository campaigns) : IIntegrationEventHandler<Payments.Contracts.CampaignSettled>
{
    public Task Handle(Payments.Contracts.CampaignSettled settled, CancellationToken cancellationToken) =>
        campaigns.TryMove(new CampaignId(settled.CampaignId), CampaignStatus.Funded, CampaignStatus.Settled, cancellationToken);
}
