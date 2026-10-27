using BuildingBlocks.Application;

namespace Pledges.Contracts;

/// <summary>
/// Asks Pledges to close a live campaign's funding because its creator cancels it. Pledges owns the
/// deadline, so Pledges decides: it closes the funding window with the same conditional update as
/// the close, or refuses with <c>campaign_closed</c> because the campaign has already ended. Asking
/// again after an accepted cancel is accepted again.
/// </summary>
public sealed record CancelFunding(Guid CampaignId) : ICommand<FundingCancelled>;

public sealed record FundingCancelled(Guid CampaignId);

/// <summary>The module's name, which keys its outbox and names its handlers.</summary>
public static class PledgesModuleName
{
    public const string Value = "Pledges";
}
