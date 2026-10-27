using BuildingBlocks.Domain;

namespace Campaigns.Domain;

/// <summary>The statuses people see. After publishing, they follow what Pledges and Payments decide.</summary>
public enum CampaignStatus
{
    Draft,
    Live,
    Cancelled,
    Funded,
    Unsuccessful,
    Settled,
    Released,
}

/// <summary>A reward tier. <see cref="Limit"/> is null for a tier with no limit.</summary>
public sealed class Tier
{
    internal Tier(TierId id, string title, long minPledgeMinor, int? limit)
    {
        Id = id;
        Title = title;
        MinPledgeMinor = minPledgeMinor;
        Limit = limit;
    }

    public TierId Id { get; private set; }

    public string Title { get; private set; }

    public long MinPledgeMinor { get; private set; }

    public int? Limit { get; private set; }
}

/// <summary>What a creator asks for when adding a tier, before the rules have checked it.</summary>
public sealed record TierDraft(string Title, long MinPledgeMinor, int? Limit);

/// <summary>A creator's campaign: a draft until it is published.</summary>
public sealed class Campaign
{
    private readonly List<Tier> _tiers = [];

    // For the persistence layer, which sets the properties itself.
#pragma warning disable CS8618
    private Campaign()
    {
    }
#pragma warning restore CS8618

    private Campaign(CampaignId id, Guid creatorId, string title, Money goal, DateTimeOffset deadline, IEnumerable<Tier> tiers)
    {
        Id = id;
        CreatorId = creatorId;
        Title = title;
        Goal = goal;
        Deadline = deadline;
        _tiers.AddRange(tiers);
        Status = CampaignStatus.Draft;
    }

    public CampaignId Id { get; private set; }

    /// <summary>The creator's user id, from the identity provider.</summary>
    public Guid CreatorId { get; private set; }

    public string Title { get; private set; }

    public Money Goal { get; private set; }

    /// <summary>An absolute instant, kept in UTC. The creator's time zone is only for display.</summary>
    public DateTimeOffset Deadline { get; private set; }

    public IReadOnlyList<Tier> Tiers => _tiers;

    public CampaignStatus Status { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Creates a draft, or returns every rule the request breaks.</summary>
    public static Result<Campaign> CreateDraft(Guid creatorId, string title, Money goal, DateTimeOffset deadline, IReadOnlyList<TierDraft> tiers)
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

        var created = tiers.Select(t => new Tier(TierId.New(), t.Title.Trim(), t.MinPledgeMinor, t.Limit));
        return Result.Success(new Campaign(CampaignId.New(), creatorId, title.Trim(), goal, deadline.ToUniversalTime(), created));
    }

    /// <summary>
    /// Opens the campaign to pledges. Only a draft is published, and only with a deadline still in
    /// the future and at least one tier. The database makes the same move conditional on DRAFT.
    /// </summary>
    public Result<CampaignStatus> Publish(DateTimeOffset now)
    {
        if (Status != CampaignStatus.Draft)
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.NotADraft);
        }

        var errors = new List<Error>();
        if (Deadline <= now)
        {
            errors.Add(CampaignErrors.DeadlineNotInFuture);
        }

        if (_tiers.Count == 0)
        {
            errors.Add(CampaignErrors.NoTiers);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<CampaignStatus>(errors);
        }

        Status = CampaignStatus.Live;
        PublishedAt = now;
        return Result.Success(Status);
    }
}
