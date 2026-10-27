using BuildingBlocks.Domain;

namespace Campaigns.Domain;

/// <summary>Every rule a campaign can break, with the code clients see.</summary>
public static class CampaignErrors
{
    public static readonly Error NegativeAmount = Error.Validation("negative_amount", "An amount cannot be negative.");
    public static readonly Error BadCurrency = Error.Validation("bad_currency", "The currency must be three upper-case letters, such as USD.");
    public static readonly Error GoalNotPositive = Error.Validation("goal_not_positive", "The goal must be more than zero.");
    public static readonly Error TierMinimumNotPositive = Error.Validation("tier_minimum_not_positive", "A tier's minimum pledge must be more than zero.");
    public static readonly Error TierLimitNotPositive = Error.Validation("tier_limit_not_positive", "A tier's limit must be more than zero, or empty for no limit.");
    public static readonly Error DuplicateTierTitle = Error.Validation("duplicate_tier_title", "Two tiers cannot have the same title.");
    public static readonly Error NotFound = Error.NotFound("campaign_not_found", "No campaign has this id.");
    public static readonly Error NotYours = Error.Forbidden("not_your_campaign", "Only the campaign's creator may do this.");
    public static readonly Error NotADraft = Error.Conflict("not_a_draft", "Only a draft can be published.");
    public static readonly Error DeadlineNotInFuture = Error.Validation("deadline_not_in_future", "The deadline must be in the future to publish.");
    public static readonly Error DeadlineTooFar = Error.Validation("deadline_too_far", "The deadline must be at most 60 days after publishing.");
    public static readonly Error NotCancellable = Error.Conflict("not_cancellable", "Only a draft or a live campaign can be cancelled.");
    public static readonly Error NoTiers = Error.Validation("no_tiers", "A campaign needs at least one tier to publish.");
}
