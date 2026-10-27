using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Domain;

using Pledges.Contracts;

namespace Campaigns.Application;

public sealed record GetCampaign(CampaignId Id) : IQuery<CampaignView>;

/// <summary>What a reader sees. Ids and statuses are strings, and the deadline is UTC, as in the API.</summary>
public sealed record CampaignView(
    string CampaignId,
    Guid CreatorId,
    string Status,
    string Title,
    long GoalMinor,
    string Currency,
    DateTime Deadline,
    long PledgedMinor,
    int Backers,
    IReadOnlyList<TierView> Tiers);

public sealed record TierView(string TierId, string Title, long MinPledgeMinor, int? Limit);

/// <summary>
/// The catalog is Campaigns' own; the total comes from Pledges, through its contract. Both are read
/// from the reader endpoint: a page may be a moment behind.
/// </summary>
public sealed class GetCampaignHandler(ICampaignReads campaigns, ICampaignTotals totals) : IQueryHandler<GetCampaign, CampaignView>
{
    public async Task<Result<CampaignView>> Handle(GetCampaign query, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.Find(query.Id, cancellationToken);
        if (campaign is null)
        {
            return Result.Failure<CampaignView>(CampaignErrors.NotFound);
        }

        var raised = await totals.Get(campaign.Id.Value, cancellationToken);
        return Result.Success(new CampaignView(
            campaign.Id.ToString(),
            campaign.CreatorId,
            campaign.Status.ToString().ToUpperInvariant(),
            campaign.Title,
            campaign.Goal.AmountMinor,
            campaign.Goal.Currency,
            campaign.Deadline.UtcDateTime,
            raised?.PledgedMinor ?? 0,
            raised?.Backers ?? 0,
            [.. campaign.Tiers.Select(t => new TierView(t.Id.ToString(), t.Title, t.MinPledgeMinor, t.Limit))]));
    }
}
