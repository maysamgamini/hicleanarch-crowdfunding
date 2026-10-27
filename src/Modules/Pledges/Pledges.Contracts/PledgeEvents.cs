using BuildingBlocks.Application;

namespace Pledges.Contracts;

/// <summary>A pledge now counts toward its campaign's total.</summary>
public sealed record PledgeActivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    Guid BackerId,
    Guid? TierId,
    long AmountMinor,
    string Currency) : IntegrationEvent(EventId, OccurredAt);

/// <summary>An active pledge's amount or tier changed. The total moved by the difference.</summary>
public sealed record PledgeChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    Guid BackerId,
    long OldAmountMinor,
    long NewAmountMinor,
    Guid? OldTierId,
    Guid? NewTierId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>A backer cancelled a pledge before the deadline. It no longer counts.</summary>
public sealed record PledgeCancelled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    Guid BackerId,
    long AmountMinor) : IntegrationEvent(EventId, OccurredAt);

/// <summary>
/// A campaign's funding was decided from the exact ledger sum after the close: FUNDED or
/// UNSUCCESSFUL. Campaigns shows it; Payments starts settling a funded campaign.
/// </summary>
public sealed record CampaignFundingDecided(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CampaignId,
    string Outcome,
    long FinalMinor,
    int Backers,
    long GoalMinor) : IntegrationEvent(EventId, OccurredAt)
{
    public const string Funded = "FUNDED";
    public const string Unsuccessful = "UNSUCCESSFUL";
}

/// <summary>A pledge of a funded campaign is to be charged, once, with its saved card.</summary>
public sealed record PledgeChargeRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    Guid BackerId,
    long AmountMinor,
    string Currency,
    string PaymentMethodRef) : IntegrationEvent(EventId, OccurredAt);
