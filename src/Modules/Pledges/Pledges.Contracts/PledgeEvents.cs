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
