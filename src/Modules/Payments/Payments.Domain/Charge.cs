namespace Payments.Domain;

public enum ChargeStatus
{
    ToCharge,
    Charging,
    Charged,
    Failed,
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

    /// <summary>A failed charge is tried again no earlier than this.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

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

    public string? LeaseOwner { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    /// <summary>When a worker last gave the job back after a batch: the job that waited longest goes next.</summary>
    public DateTimeOffset? LastTurnAt { get; private set; }

    /// <summary>
    /// No worker claims the job before this: set when a pass gives it back with nothing it can do
    /// yet, or after the PSP failed, so a waiting campaign never keeps the workers from the next one.
    /// </summary>
    public DateTimeOffset? NotBefore { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
