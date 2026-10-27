using BuildingBlocks.Domain;

namespace Pledges.Domain;

/// <summary>Every rule pledging can refuse, with the reason clients see.</summary>
public static class PledgeErrors
{
    public static readonly Error CampaignClosed = Error.Conflict("campaign_closed", "Pledging on this campaign has closed.");
    public static readonly Error FundingNotOpen = Error.Conflict("funding_not_open", "This campaign's funding is not open yet; try again in a moment.");
}
