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
/// when, and how it was decided. Its facts are copied from the campaign when it is published.
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

    /// <summary>
    /// Round 1's snapshot of the ledger's sum. Since Round 2 (lesson s3-l05) a window opens with 0 here
    /// and nothing writes or reads it again: the ledger is the total. The column stays until a later
    /// migration drops it, after a release in which no running version still inserts it.
    /// </summary>
    public long PledgedMinor { get; private set; }

    /// <summary>Round 1's backer count beside <see cref="PledgedMinor"/>, kept and frozen the same way.</summary>
    public int Backers { get; private set; }

    /// <summary>The exact sum after the close, which decides the campaign.</summary>
    public long? FinalMinor { get; private set; }

    /// <summary>When pledging stopped: the close's ENDED, or the creator's cancel. The fence's wait starts here.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The last pledge marked TO_CHARGE so far: the cursor a resumed marking starts after (lesson s3-a04).</summary>
    public Guid? MarkedThrough { get; private set; }

    /// <summary>
    /// When every pledge of a decided or cancelled campaign was marked TO_CHARGE or released; null while
    /// a funded campaign's marking, or a cancelled one's release, is unfinished.
    /// </summary>
    public DateTimeOffset? MarkingFinishedAt { get; private set; }

    /// <summary>When pledging opened. Every ledger entry of the campaign is at or after it, so sums can name the months to read.</summary>
    public DateTimeOffset OpenedAt { get; private set; }

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

    public static TierCounter For(Guid tierId, Guid campaignId, long minPledgeMinor, int? limitQty) => new()
    {
        TierId = tierId,
        CampaignId = campaignId,
        MinPledgeMinor = minPledgeMinor,
        LimitQty = limitQty,
    };
}
