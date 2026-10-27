using BuildingBlocks.Application;

namespace Payments.Contracts;

/// <summary>A pledge was charged. At most one per pledge, ever.</summary>
public sealed record PledgeChargeSucceeded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    int AttemptNo,
    string PspPaymentId,
    long AmountMinor,
    Guid BackerId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>An attempt to charge a pledge was declined. <see cref="Final"/>: no attempt is left.</summary>
public sealed record PledgeChargeFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    int AttemptNo,
    string DeclineCode,
    bool Final,
    Guid BackerId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>Every charge of a funded campaign has ended, charged or failed.</summary>
public sealed record CampaignSettled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CampaignId,
    int Charged,
    int Failed) : IntegrationEvent(EventId, OccurredAt);
