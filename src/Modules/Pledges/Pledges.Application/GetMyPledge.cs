using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Pledges.Domain;

namespace Pledges.Application;

/// <summary>A backer's own pledge. Read from the writer, always: a backer must see what they just did.</summary>
public sealed record GetMyPledge(Guid PledgeId, Guid BackerId) : IQuery<PledgeView>;

public sealed record PledgeView(string PledgeId, string CampaignId, string Status, long AmountMinor, string? TierId, DateTimeOffset CreatedAt);

public sealed class GetMyPledgeHandler(IPledges pledges) : IQueryHandler<GetMyPledge, PledgeView>
{
    public async Task<Result<PledgeView>> Handle(GetMyPledge query, CancellationToken cancellationToken)
    {
        var pledge = await pledges.Find(query.PledgeId, cancellationToken);
        if (pledge is null || pledge.BackerId != query.BackerId)
        {
            return Result.Failure<PledgeView>(PledgeErrors.PledgeNotFound);
        }

        return Result.Success(PledgeViews.Of(pledge));
    }
}

public static class PledgeViews
{
    public static PledgeView Of(Pledge pledge) => new(
        pledge.PublicId,
        PublicId.Format("cmp", pledge.CampaignId),
        StatusNames.Of(pledge.Status),
        pledge.AmountMinor,
        pledge.TierId is { } tier ? PublicId.Format("tir", tier) : null,
        pledge.CreatedAt);
}

/// <summary>Statuses as the API and the database spell them: PENDING_SETUP, TO_CHARGE.</summary>
public static class StatusNames
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(text.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);
}
