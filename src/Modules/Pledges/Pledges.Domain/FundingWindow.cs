namespace Pledges.Domain;

/// <summary>Where a campaign's funding stands. Only Pledges moves it.</summary>
public enum FundingStatus
{
    Live,
    Cancelled,
    Ended,
    Funded,
    Unsuccessful,
}

/// <summary>
/// Everything pledging needs to know about one campaign, in one row: whether it is open, until
/// when, and the total so far. Its facts are copied from the campaign when it is published.
/// </summary>
public sealed class FundingWindow
{
    // For the persistence layer, which sets the properties itself.
#pragma warning disable CS8618
    private FundingWindow()
    {
    }
#pragma warning restore CS8618

    public Guid CampaignId { get; private set; }

    public FundingStatus Status { get; private set; }

    /// <summary>Pledging is open while the database's <c>now()</c> is before this instant.</summary>
    public DateTimeOffset Deadline { get; private set; }

    public long GoalMinor { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The snapshot of the ledger's sum, updated in the same transaction as every entry.</summary>
    public long PledgedMinor { get; private set; }

    public int Backers { get; private set; }

    /// <summary>The exact sum after the close, which decides the campaign.</summary>
    public long? FinalMinor { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The last pledge marked TO_CHARGE so far: the cursor a resumed marking starts after (lesson s3-a04).</summary>
    public Guid? MarkedThrough { get; private set; }

    /// <summary>When every pledge of a decided campaign was marked; null while a funded campaign's marking is unfinished.</summary>
    public DateTimeOffset? MarkingFinishedAt { get; private set; }

    public static FundingWindow Open(Guid campaignId, DateTimeOffset deadline, long goalMinor, string currency) => new()
    {
        CampaignId = campaignId,
        Status = FundingStatus.Live,
        Deadline = deadline.ToUniversalTime(),
        GoalMinor = goalMinor,
        Currency = currency,
    };
}

/// <summary>
/// A tier's places, as Pledges counts them. <see cref="LimitQty"/> is null for a tier with no
/// limit, which is never counted.
/// </summary>
public sealed class TierCounter
{
    private TierCounter()
    {
    }

    public Guid TierId { get; private set; }

    public Guid CampaignId { get; private set; }

    public long MinPledgeMinor { get; private set; }

    public int? LimitQty { get; private set; }

    /// <summary>Places taken by active pledges, until the deadline.</summary>
    public int Claimed { get; private set; }

    /// <summary>Places kept for pledges waiting for their card, for 15 minutes each.</summary>
    public int Held { get; private set; }

    /// <summary>Only a tier with a limit is counted.</summary>
    public bool IsLimited => LimitQty is not null;

    /// <summary>
    /// The domain's statement of lesson 6's rule, on the counts it was read with: no room left. It
    /// gives an early, clear refusal; the conditional update in the database is the guarantee.
    /// </summary>
    public bool LooksSoldOut => LimitQty is { } limit && Claimed + Held >= limit;

    public static TierCounter For(Guid tierId, Guid campaignId, long minPledgeMinor, int? limitQty) => new()
    {
        TierId = tierId,
        CampaignId = campaignId,
        MinPledgeMinor = minPledgeMinor,
        LimitQty = limitQty,
    };
}
