using BuildingBlocks.Application;

namespace Campaigns.Contracts;

/// <summary>
/// A campaign was cancelled by its creator. Pledges already knows: it accepted the cancel before
/// this was published. This event is for those who tell people about it.
/// </summary>
public sealed record CampaignCancelled(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CampaignId,
    Guid CreatorId) : IntegrationEvent(EventId, OccurredAt);
