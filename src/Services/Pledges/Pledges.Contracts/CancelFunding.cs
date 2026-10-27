using BuildingBlocks.Application;

namespace Pledges.Contracts;

/// <summary>
/// Asks Pledges to close a live campaign's funding because its creator cancels it. Pledges owns the
/// deadline, so Pledges decides: it closes the funding window with the same conditional update as
/// the close, or refuses with <c>campaign_closed</c> because the campaign has already ended. Asking
/// again after an accepted cancel is accepted again.
/// </summary>
public sealed record CancelFunding(Guid CampaignId) : ICommand<CancelFundingAccepted>;

public sealed record CancelFundingAccepted(Guid CampaignId);

/// <summary>
/// Pledges accepted a cancel and closed the campaign's funding. Campaigns listens, so an accepted
/// cancel always reaches the catalog, even if the request that asked for it failed afterwards.
/// </summary>
public sealed record FundingCancelled(Guid EventId, DateTimeOffset OccurredAt, Guid CampaignId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>
/// A cancelled campaign's pledge was let go: an active one is RELEASED, one still waiting for its card
/// EXPIRED, with its place given back. It will never be charged. Published in the same transaction as
/// the move, so every backer of a cancelled campaign is told, including one whose pledge committed
/// just after the cancel.
/// </summary>
public sealed record PledgeReleased(Guid EventId, DateTimeOffset OccurredAt, Guid PledgeId, Guid CampaignId, Guid BackerId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>The module's name, which keys its outbox and names its handlers.</summary>
public static class PledgesModuleName
{
    public const string Value = "Pledges";
}
