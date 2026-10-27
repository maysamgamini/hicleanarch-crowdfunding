using BuildingBlocks.Application;

using Campaigns.Contracts;

using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// A campaign went live: open its funding window and count its tiers. Pledges copies the deadline,
/// the goal and the limits from the event and never reads Campaigns' tables. The inbox drops a
/// repeat of the same event, and the insert itself does nothing the second time. The window keeps
/// the creator, so Pledges can check a creator's cancel itself (lesson s5-l05).
/// </summary>
public sealed class OpenFundingWindow(IFundingWindows windows, IFundingWindowCreators creators) : IIntegrationEventHandler<CampaignPublished>
{
    public async Task Handle(CampaignPublished published, CancellationToken cancellationToken)
    {
        await windows.TryOpen(
            FundingWindow.Open(published.CampaignId, published.Deadline, published.GoalMinor, published.Currency),
            [.. published.Tiers.Select(t => TierCounter.For(t.TierId, published.CampaignId, t.MinPledgeMinor, t.LimitQty))],
            cancellationToken);
        await creators.TryRecord(published.CampaignId, published.CreatorId, cancellationToken);
    }
}
