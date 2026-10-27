using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Pledges.Domain;

namespace Pledges.Application;

/// <summary>The small reply every campaign page polls: the total, the backers, the places left.</summary>
public sealed record GetPublicTotals(Guid CampaignId) : IQuery<PublicTotalsReply>;

/// <summary>
/// What the read port returns: the funding window's status, deadline and final sum, with the display
/// totals the aggregator last computed (lesson s3-l05) and when it computed them.
/// </summary>
public sealed record FundingSnapshot(
    Guid CampaignId,
    FundingStatus Status,
    DateTimeOffset Deadline,
    long GoalMinor,
    string Currency,
    long PledgedMinor,
    int Backers,
    long? FinalMinor,
    IReadOnlyList<TierPlaces> Tiers,
    DateTimeOffset AsOf);

/// <summary><c>Remaining</c> is null for a tier with no limit.</summary>
public sealed record TierPlaces(Guid TierId, int? LimitQty, int? Remaining);

/// <summary>Reads a funding window for display. Nothing read here decides anything.</summary>
public interface IPublicTotalsReads
{
    Task<FundingSnapshot?> Find(Guid campaignId, CancellationToken cancellationToken);
}

/// <summary>
/// The reply, and how long a cache may keep it. <c>exact</c> is false until the deadline's exact
/// sum replaces the display total; <c>as_of</c> says when the numbers were read.
/// </summary>
public sealed record PublicTotalsReply(PublicTotalsView View, int MaxAgeSeconds);

public sealed record PublicTotalsView(
    string CampaignId,
    string Status,
    bool PledgingOpen,
    long PledgedMinor,
    int Backers,
    long GoalMinor,
    string Currency,
    DateTime Deadline,
    IReadOnlyList<TierRemaining> Tiers,
    bool Exact,
    DateTime AsOf);

/// <summary><c>Remaining</c> is null for a tier with no limit.</summary>
public sealed record TierRemaining(string TierId, int? Limit, int? Remaining);

public sealed class GetPublicTotalsHandler(IPublicTotalsReads totals, TimeProvider clock) : IQueryHandler<GetPublicTotals, PublicTotalsReply>
{
    /// <summary>How long CloudFront may serve the same reply (lesson s3-a02).</summary>
    public const int MaxAgeSeconds = 2;

    public async Task<Result<PublicTotalsReply>> Handle(GetPublicTotals query, CancellationToken cancellationToken)
    {
        var window = await totals.Find(query.CampaignId, cancellationToken);
        if (window is null)
        {
            return Result.Failure<PublicTotalsReply>(PledgeErrors.CampaignNotFound);
        }

        var now = clock.GetUtcNow();
        var open = window.Status == FundingStatus.Live && now < window.Deadline;

        // At the deadline the page flips at once, before the close job has run: a live status is
        // shown as ENDED, and a reply cached before the deadline never outlives it.
        var status = window.Status == FundingStatus.Live && !open ? FundingStatus.Ended : window.Status;
        var maxAge = open ? (int)Math.Min(MaxAgeSeconds, Math.Floor((window.Deadline - now).TotalSeconds)) : MaxAgeSeconds;

        var view = new PublicTotalsView(
            PublicId.Format("cmp", window.CampaignId),
            status.ToString().ToUpperInvariant(),
            open,
            window.FinalMinor ?? window.PledgedMinor,
            window.Backers,
            window.GoalMinor,
            window.Currency,
            window.Deadline.UtcDateTime,
            [.. window.Tiers.Select(t => new TierRemaining(PublicId.Format("tir", t.TierId), t.LimitQty, t.Remaining))],
            window.FinalMinor is not null,
            (window.FinalMinor is not null ? now : window.AsOf).UtcDateTime);
        return Result.Success(new PublicTotalsReply(view, maxAge));
    }
}
