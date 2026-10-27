namespace Payments.Domain;

public enum ChargeStatus
{
    ToCharge,
    Charging,
    Charged,

    /// <summary>Declined, inside its 7-day recovery window (lesson s3-a04).</summary>
    PaymentFailed,

    /// <summary>The window ended, or the attempts ran out: never charged, and the campaign stays funded.</summary>
    Dropped,

    /// <summary>Charged, then given back to the backer. The money ledger records the refund as a journal of its own.</summary>
    Refunded,
}

public enum AttemptStatus
{
    InFlight,
    Succeeded,
    Failed,
}

/// <summary>One pledge of a funded campaign, to be charged at most once.</summary>
public sealed class Charge
{
#pragma warning disable CS8618
    private Charge()
    {
    }
#pragma warning restore CS8618

    /// <summary>The pledge it charges: one charge per pledge.</summary>
    public Guid PledgeId { get; private set; }

    public Guid CampaignId { get; private set; }

    public Guid BackerId { get; private set; }

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; }

    public string PaymentMethodRef { get; private set; }

    public ChargeStatus Status { get; private set; }

    /// <summary>The number of the latest attempt; 0 before the first.</summary>
    public int AttemptNo { get; private set; }

    public string? PspPaymentId { get; private set; }

    /// <summary>Set when a charged pledge is given back.</summary>
    public string? PspRefundId { get; private set; }

    /// <summary>A failed charge is tried again no earlier than this.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>The first decline: the recovery window runs from here.</summary>
    public DateTimeOffset? PaymentFailedAt { get; private set; }

    /// <summary>Why the latest attempt was declined, which decides what the backer is asked to do.</summary>
    public string? LastDeclineCode { get; private set; }

    /// <summary>The PSP's advice on trying the latest declined attempt again, when it gave one.</summary>
    public string? LastAdviceCode { get; private set; }

    /// <summary>
    /// The settlement worker that holds the charge for its batch, until <see cref="ClaimedUntil"/>.
    /// Workers share a campaign by claiming its charges, not its job, so no other worker takes them meanwhile.
    /// </summary>
    public string? ClaimedBy { get; private set; }

    public DateTimeOffset? ClaimedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Charge For(Guid pledgeId, Guid campaignId, Guid backerId, long amountMinor, string currency, string paymentMethodRef) => new()
    {
        PledgeId = pledgeId,
        CampaignId = campaignId,
        BackerId = backerId,
        AmountMinor = amountMinor,
        Currency = currency,
        PaymentMethodRef = paymentMethodRef,
        Status = ChargeStatus.ToCharge,
    };

    /// <summary>The key sent to the PSP for attempt <paramref name="attemptNo"/>: the same for a retry of that attempt, new for the next.</summary>
    public static string KeyFor(string pledgePublicId, int attemptNo) => $"{pledgePublicId}:charge:{attemptNo}";
}

/// <summary>One numbered try to charge a pledge, recorded before the PSP is called.</summary>
public sealed class ChargeAttempt
{
#pragma warning disable CS8618
    private ChargeAttempt()
    {
    }
#pragma warning restore CS8618

    public Guid PledgeId { get; private set; }

    public int AttemptNo { get; private set; }

    public string IdempotencyKey { get; private set; }

    public AttemptStatus Status { get; private set; }

    public string? PspPaymentId { get; private set; }

    public string? DeclineCode { get; private set; }

    public string? AdviceCode { get; private set; }

    /// <summary>Confirmed by the backer on our page, with the bank's check, not charged off-session.</summary>
    public bool OnSession { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public enum SettlementStatus
{
    Pending,
    Running,
    Done,
}

/// <summary>A funded campaign's settlement: the one place that knows how far it has got.</summary>
public sealed class SettlementJob
{
    private SettlementJob()
    {
    }

    public Guid CampaignId { get; private set; }

    public SettlementStatus Status { get; private set; }

    /// <summary>How many charges the decision asked for; the job is done when all of them have ended.</summary>
    public int ExpectedCharges { get; private set; }

    /// <summary>
    /// The job's own lease before s3-l16, when one worker held a whole campaign. No longer written;
    /// the columns stay until no task runs the older code, then a later migration drops them.
    /// </summary>
    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    /// <summary>When a worker last took a turn at the job: the job that waited longest goes next.</summary>
    public DateTimeOffset? LastTurnAt { get; private set; }

    /// <summary>
    /// No worker takes a turn before this: set when a pass ends with nothing anyone can do yet, so a
    /// waiting campaign never keeps the workers from the next one.
    /// </summary>
    public DateTimeOffset? NotBefore { get; private set; }

    /// <summary>
    /// After the PSP could not be reached, or the circuit was open: no turn before this. Kept apart
    /// from <see cref="NotBefore"/>, so another worker's pass ending at the same time cannot undo the pause.
    /// </summary>
    public DateTimeOffset? PausedUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
