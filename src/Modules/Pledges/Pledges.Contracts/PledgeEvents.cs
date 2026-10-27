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
