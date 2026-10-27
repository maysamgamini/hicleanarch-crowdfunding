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
    public static readonly Error BelowGoalOnLastDay = Error.Conflict("would_drop_below_goal", "In its last 24 hours, a funded campaign cannot be taken below its goal by a cancel or a decrease.");
    public static readonly Error TierSoldOut = Error.Conflict("tier_sold_out", "This tier has no place left. Nothing was created.");
    public static readonly Error CardSetupFailed = Error.Conflict("card_setup_unavailable", "Saving a card is not possible right now; nothing was created. Try again.");
    public static readonly Error NotWaitingForCard = Error.Conflict("pledge_not_waiting_for_card", "This pledge is not waiting for a card, or its hold has ended.");
    public static readonly Error CreatorUnknown = Error.Conflict("creator_unknown", "Pledges does not know this campaign's creator yet; try again later.");
    public static readonly Error NotYourCampaign = Error.Forbidden("not_your_campaign", "Only the campaign's creator may do this.");
    public static readonly Error FundingNotOpen = Error.Conflict("funding_not_open", "This campaign's funding is not open yet; try again in a moment.");
}
