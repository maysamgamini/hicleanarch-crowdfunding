using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Domain;

namespace Campaigns.Application;

/// <summary>A campaign as <paramref name="CallerId"/> may see it; null for someone not signed in.</summary>
public sealed record GetCampaign(CampaignId Id, Guid? CallerId) : IQuery<CampaignView>;

/// <summary>
/// What a reader sees. Ids and statuses are strings, and the deadline is UTC, as in the API. The
/// creator's user id is not in it: it is the key to their account, and no reader needs it.
/// </summary>
public sealed record CampaignView(
    string CampaignId,
    string Status,
    string Title,
    long GoalMinor,
    string Currency,
    DateTime Deadline,
    IReadOnlyList<TierView> Tiers);

public sealed record TierView(string TierId, string Title, long MinPledgeMinor, int? Limit);

/// <summary>
/// The catalog part of a campaign: Campaigns' own data, which changes rarely. It is cached for
/// five minutes and dropped from the cache by every event that changes it.
/// </summary>
public sealed record GetCampaignCatalog(CampaignId Id) : IQuery<CampaignCatalog>, ICacheableQuery
{
    public string CacheKey => Key(Id);

    public TimeSpan TimeToLive => TimeSpan.FromMinutes(5);

    public static string Key(CampaignId id) => $"campaigns:catalog:{id}";
}

public sealed record CampaignCatalog(
    string CampaignId,
    Guid CreatorId,
    string Status,
    string Title,
    long GoalMinor,
    string Currency,
    DateTime Deadline,
    IReadOnlyList<TierView> Tiers);

/// <summary>
/// Loads from the writer, not the reader: what it returns is kept in the cache for five minutes, so
/// a row a replica has not caught up with yet must never be the one cached.
/// </summary>
[ReadsTheWriter]
public sealed class GetCampaignCatalogHandler(ICampaignRepository campaigns) : IQueryHandler<GetCampaignCatalog, CampaignCatalog>
{
    public async Task<Result<CampaignCatalog>> Handle(GetCampaignCatalog query, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.Find(query.Id, cancellationToken);
        if (campaign is null)
        {
            return Result.Failure<CampaignCatalog>(CampaignErrors.NotFound);
        }

        return Result.Success(new CampaignCatalog(
            campaign.Id.ToString(),
            campaign.CreatorId,
            campaign.Status.ToString().ToUpperInvariant(),
            campaign.Title,
            campaign.Goal.AmountMinor,
            campaign.Goal.Currency,
            campaign.Deadline.UtcDateTime,
            [.. campaign.Tiers.Select(t => new TierView(t.Id.ToString(), t.Title, t.MinPledgeMinor, t.Limit))]));
    }
}

/// <summary>
/// The catalog, which may come from the cache. The totals are not in it (lesson s5-l08): a page reads
/// them from Pledges' <c>GET /v1/campaigns/{id}/totals</c>, behind the CDN, so Campaigns asks Pledges
/// nothing. A draft is its creator's alone: anyone else is told there is no such campaign.
/// </summary>
public sealed class GetCampaignHandler(
    IQueryHandler<GetCampaignCatalog, CampaignCatalog> catalog) : IQueryHandler<GetCampaign, CampaignView>
{
    public async Task<Result<CampaignView>> Handle(GetCampaign query, CancellationToken cancellationToken)
    {
        var found = await catalog.Handle(new GetCampaignCatalog(query.Id), cancellationToken);
        if (!found.IsSuccess)
        {
            return Result.Failure<CampaignView>(found.Errors);
        }

        var c = found.Value;
        if (string.Equals(c.Status, "DRAFT", StringComparison.Ordinal) && c.CreatorId != query.CallerId)
        {
            return Result.Failure<CampaignView>(CampaignErrors.NotFound);
        }

        return Result.Success(new CampaignView(c.CampaignId, c.Status, c.Title, c.GoalMinor, c.Currency, c.Deadline, c.Tiers));
    }
}
