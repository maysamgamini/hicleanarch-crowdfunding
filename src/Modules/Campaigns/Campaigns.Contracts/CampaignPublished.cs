using BuildingBlocks.Application;

namespace Campaigns.Contracts;

/// <summary>
/// A campaign went live. It carries the goal, the deadline and the tier limits, because the
/// module that takes pledges copies them; a listener never reads Campaigns' tables.
/// </summary>
public sealed record CampaignPublished(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CampaignId,
    Guid CreatorId,
    long GoalMinor,
    string Currency,
    DateTimeOffset Deadline,
    IReadOnlyList<PublishedTier> Tiers) : IntegrationEvent(EventId, OccurredAt);

/// <summary>A tier as listeners need it. <see cref="LimitQty"/> is null for a tier with no limit.</summary>
public sealed record PublishedTier(Guid TierId, long MinPledgeMinor, int? LimitQty);

/// <summary>The module's name, which keys its outbox and names its handlers.</summary>
public static class CampaignsModuleName
{
    public const string Value = "Campaigns";
}
