using BuildingBlocks.Domain;

namespace Campaigns.Domain;

public enum CampaignStatus
{
    Draft,
}

/// <summary>A reward tier. <see cref="Limit"/> is null for a tier with no limit.</summary>
public sealed record Tier(TierId Id, string Title, long MinPledgeMinor, int? Limit);

/// <summary>What a creator asks for when adding a tier, before the rules have checked it.</summary>
public sealed record TierDraft(string Title, long MinPledgeMinor, int? Limit);

/// <summary>A creator's campaign. In this lesson it can only be created as a draft.</summary>
public sealed class Campaign
{
    private Campaign(CampaignId id, string title, Money goal, DateTimeOffset deadline, IReadOnlyList<Tier> tiers)
    {
        Id = id;
        Title = title;
        Goal = goal;
        Deadline = deadline;
        Tiers = tiers;
        Status = CampaignStatus.Draft;
    }

    public CampaignId Id { get; }

    public string Title { get; }

    public Money Goal { get; }

    /// <summary>An absolute instant, kept in UTC. The creator's time zone is only for display.</summary>
    public DateTimeOffset Deadline { get; }

    public IReadOnlyList<Tier> Tiers { get; }

    public CampaignStatus Status { get; }

    /// <summary>Creates a draft, or returns every rule the request breaks.</summary>
    public static Result<Campaign> CreateDraft(string title, Money goal, DateTimeOffset deadline, IReadOnlyList<TierDraft> tiers)
    {
        var errors = new List<Error>();
        if (goal.AmountMinor <= 0)
        {
            errors.Add(CampaignErrors.GoalNotPositive);
        }

        if (tiers.Any(t => t.MinPledgeMinor <= 0))
        {
            errors.Add(CampaignErrors.TierMinimumNotPositive);
        }

        if (tiers.Any(t => t.Limit is <= 0))
        {
            errors.Add(CampaignErrors.TierLimitNotPositive);
        }

        if (tiers.Select(t => t.Title.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tiers.Count)
        {
            errors.Add(CampaignErrors.DuplicateTierTitle);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<Campaign>(errors);
        }

        var created = tiers.Select(t => new Tier(TierId.New(), t.Title.Trim(), t.MinPledgeMinor, t.Limit)).ToList();
        return Result.Success(new Campaign(CampaignId.New(), title.Trim(), goal, deadline.ToUniversalTime(), created));
    }
}
