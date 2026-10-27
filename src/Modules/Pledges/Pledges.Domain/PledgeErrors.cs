using BuildingBlocks.Domain;

namespace Pledges.Domain;

/// <summary>Every rule pledging can refuse, with the reason clients see.</summary>
public static class PledgeErrors
{
    public static readonly Error CampaignClosed = Error.Conflict("campaign_closed", "Pledging on this campaign has closed.");
    public static readonly Error CampaignNotFound = Error.NotFound("campaign_not_found", "No campaign is open for pledges with this id.");
    public static readonly Error PledgeNotFound = Error.NotFound("pledge_not_found", "You have no pledge with this id.");
    public static readonly Error AlreadyBacking = Error.Conflict("already_backing", "You already back this campaign; change your pledge instead.");
    public static readonly Error AmountNotPositive = Error.Validation("amount_not_positive", "A pledge must be more than zero.");
    public static readonly Error BelowTierMinimum = Error.Validation("below_tier_minimum", "The amount is below the tier's minimum pledge.");
    public static readonly Error UnknownTier = Error.Validation("unknown_tier", "This campaign has no such tier.");
    public static readonly Error PaymentMethodRequired = Error.Validation("payment_method_required", "Pledge with a saved card (payment_method_id).");
    public static readonly Error NotActive = Error.Conflict("pledge_not_active", "Only an active pledge can be changed or cancelled.");
    public static readonly Error NoChange = Error.Validation("no_change", "The pledge already has this amount and tier.");
    public static readonly Error TierSoldOut = Error.Conflict("tier_sold_out", "This tier has no place left. Nothing was created.");
    public static readonly Error FundingNotOpen = Error.Conflict("funding_not_open", "This campaign's funding is not open yet; try again in a moment.");
}
