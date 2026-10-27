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

/// <summary>
/// An attempt to charge a pledge was declined. <see cref="Final"/>: no attempt is left.
/// <see cref="RetriedOnItsOwn"/>: the same card is tried again later without the backer; otherwise only
/// the backer can save the pledge (a new card, or a confirmation). Added later: an older event reads false.
/// </summary>
public sealed record PledgeChargeFailed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PledgeId,
    Guid CampaignId,
    int AttemptNo,
    string DeclineCode,
    bool Final,
    Guid BackerId,
    bool RetriedOnItsOwn = false) : IntegrationEvent(EventId, OccurredAt);

/// <summary>Every charge of a funded campaign has ended, charged or failed.</summary>
public sealed record CampaignSettled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CampaignId,
    int Charged,
    int Failed) : IntegrationEvent(EventId, OccurredAt);
