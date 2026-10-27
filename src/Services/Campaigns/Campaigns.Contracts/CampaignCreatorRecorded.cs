using BuildingBlocks.Application;

namespace Campaigns.Contracts;

/// <summary>
/// Temporary (lesson s5-l05): a LIVE campaign's creator, published once by the one-off
/// <c>record-creators</c> command, so a module that started storing the creator after the campaign
/// went live can fill it in. New campaigns need no such event: <see cref="CampaignPublished"/>
/// carries the creator. Removed when nothing sends or reads it any more (lesson s5-l11).
/// </summary>
public sealed record CampaignCreatorRecorded(Guid EventId, DateTimeOffset OccurredAt, Guid CampaignId, Guid CreatorId)
    : IntegrationEvent(EventId, OccurredAt);
