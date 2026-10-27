using BuildingBlocks.Application;

using Campaigns.Domain;

using Pledges.Contracts;

namespace Campaigns.Application;

/// <summary>
/// The status people see follows Pledges' decision. The moves are conditional on LIVE, so a repeat of
/// the event, or one about a campaign already moved (settled before its decision arrived), changes
/// nothing. Pledges releases an unsuccessful campaign's pledges in the decision's own transaction, so
/// the catalog makes both of its moves, LIVE to UNSUCCESSFUL to RELEASED, in this handler's one.
/// </summary>
public sealed class FollowFundingDecision(ICampaignRepository campaigns) : IIntegrationEventHandler<CampaignFundingDecided>
{
    public async Task Handle(CampaignFundingDecided decided, CancellationToken cancellationToken)
    {
        var id = new CampaignId(decided.CampaignId);
        if (decided.Outcome == CampaignFundingDecided.Funded)
        {
            await campaigns.TryMove(id, CampaignStatus.Live, CampaignStatus.Funded, cancellationToken);
            return;
        }

        await campaigns.TryMove(id, CampaignStatus.Live, CampaignStatus.Unsuccessful, cancellationToken);
        await campaigns.TryMove(id, CampaignStatus.Unsuccessful, CampaignStatus.Released, cancellationToken);
    }
}

/// <summary>
/// Every charge of a funded campaign has ended: to SETTLED. A settlement implies the campaign was
/// funded, so it moves from FUNDED or from LIVE: events can arrive in any order, and the inbox records
/// this one even when it moves nothing, so a settlement handled before its decision must not wait for it.
/// </summary>
public sealed class FollowSettlement(ICampaignRepository campaigns) : IIntegrationEventHandler<Payments.Contracts.CampaignSettled>
{
    private static readonly CampaignStatus[] SettledFrom = [CampaignStatus.Live, CampaignStatus.Funded];

    public Task Handle(Payments.Contracts.CampaignSettled settled, CancellationToken cancellationToken) =>
        campaigns.TryMove(new CampaignId(settled.CampaignId), SettledFrom, CampaignStatus.Settled, cancellationToken);
}
