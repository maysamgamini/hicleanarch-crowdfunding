using BuildingBlocks.Domain;

namespace Payments.Domain;

/// <summary>
/// The money ledger's accounts. The platform-wide ones (the PSP balance, processing fees, the
/// platform's fee) and the per-campaign ones (clearing, the creator's payable and reserve) are
/// told apart by the journal's campaign.
/// </summary>
public static class Accounts
{
    /// <summary>Money the PSP holds for us.</summary>
    public const string PspBalance = "PSP_BALANCE";

    /// <summary>What the PSP kept as its fees.</summary>
    public const string ProcessingFees = "PROCESSING_FEES";

    /// <summary>A campaign's collected money, until it is split.</summary>
    public const string Clearing = "CLEARING";

    /// <summary>The platform's own fee.</summary>
    public const string PlatformFee = "PLATFORM_FEE";

    /// <summary>What the creator is owed and has not been paid.</summary>
    public const string CreatorPayable = "CREATOR_PAYABLE";

    /// <summary>The creator's money held back for refunds and disputes.</summary>
    public const string CreatorReserve = "CREATOR_RESERVE";
}

/// <summary>What a journal records. With its source, it names the journal once: a replay posts nothing.</summary>
public static class JournalKinds
{
    public const string Charge = "CHARGE";
    public const string Refund = "REFUND";
    public const string Split = "SPLIT";
    public const string Reserve = "RESERVE";
    public const string Payout = "PAYOUT";
}

/// <summary>One line of a journal. A debit is positive, a credit negative.</summary>
public sealed record MoneyLine(string Account, long AmountMinor);

/// <summary>
/// One movement of money: lines on named accounts that sum to zero. Money never appears or
/// disappears; it moves. A journal is never changed: a mistake or a refund is a new journal.
/// </summary>
public sealed record MoneyJournal
{
    private MoneyJournal(Guid campaignId, string kind, string sourceRef, string currency, IReadOnlyList<MoneyLine> lines)
    {
        CampaignId = campaignId;
        Kind = kind;
        SourceRef = sourceRef;
        Currency = currency;
        Lines = lines;
    }

    public Guid CampaignId { get; }

    public string Kind { get; }

    /// <summary>What caused it: the PSP's payment, refund or transfer, or the campaign for a split.</summary>
    public string SourceRef { get; }

    public string Currency { get; }

    public IReadOnlyList<MoneyLine> Lines { get; }

    /// <summary>A journal of the non-zero lines, or the rule it breaks.</summary>
    public static Result<MoneyJournal> Create(Guid campaignId, string kind, string sourceRef, string currency, IEnumerable<MoneyLine> lines)
    {
        var kept = lines.Where(l => l.AmountMinor != 0).ToList();
        if (kept.Count < 2)
        {
            return Result.Failure<MoneyJournal>(PaymentErrors.JournalTooShort);
        }

        if (kept.Sum(l => l.AmountMinor) != 0)
        {
            return Result.Failure<MoneyJournal>(PaymentErrors.JournalUnbalanced);
        }

        return Result.Success(new MoneyJournal(campaignId, kind, sourceRef, currency, kept));
    }

    /// <summary>
    /// A charge succeeded: the PSP holds the amount less its fee, the fee is recorded, and the
    /// whole amount is credited to the campaign's clearing account.
    /// </summary>
    public static MoneyJournal ForCharge(Guid campaignId, string pspPaymentId, long amountMinor, long feeMinor, string currency) =>
        Create(campaignId, JournalKinds.Charge, pspPaymentId, currency,
        [
            new(Accounts.PspBalance, amountMinor - feeMinor),
            new(Accounts.ProcessingFees, feeMinor),
            new(Accounts.Clearing, -amountMinor),
        ]).Value;

    /// <summary>
    /// A refund. Before payout it comes out of clearing. After payout it comes out of the reserve
    /// first, then the creator's balance, which can go negative. The PSP keeps its fee either way.
    /// </summary>
    public static MoneyJournal ForRefund(Guid campaignId, string pspRefundId, long amountMinor, string currency, bool afterPayout, long reserveLeftMinor)
    {
        if (!afterPayout)
        {
            return Create(campaignId, JournalKinds.Refund, pspRefundId, currency,
                [new(Accounts.Clearing, amountMinor), new(Accounts.PspBalance, -amountMinor)]).Value;
        }

        var fromReserve = Math.Min(amountMinor, Math.Max(0, reserveLeftMinor));
        return Create(campaignId, JournalKinds.Refund, pspRefundId, currency,
        [
            new(Accounts.CreatorReserve, fromReserve),
            new(Accounts.CreatorPayable, amountMinor - fromReserve),
            new(Accounts.PspBalance, -amountMinor),
        ]).Value;
    }

    /// <summary>
    /// The split, once the campaign is reconciled: clearing is emptied into the platform's fee, the
    /// processing fees charged back to the creator, and what the creator is owed.
    /// </summary>
    public static Result<MoneyJournal> ForSplit(Guid campaignId, string currency, long clearingMinor, long processingFeesMinor, long platformFeeMinor) =>
        Create(campaignId, JournalKinds.Split, SplitRef(campaignId), currency,
        [
            new(Accounts.Clearing, clearingMinor),
            new(Accounts.PlatformFee, -platformFeeMinor),
            new(Accounts.ProcessingFees, -processingFeesMinor),
            new(Accounts.CreatorPayable, -(clearingMinor - platformFeeMinor - processingFeesMinor)),
        ]);

    /// <summary>Part of what the creator is owed is held back.</summary>
    public static Result<MoneyJournal> ForReserve(Guid campaignId, string currency, long reserveMinor) =>
        Create(campaignId, JournalKinds.Reserve, SplitRef(campaignId), currency,
            [new(Accounts.CreatorPayable, reserveMinor), new(Accounts.CreatorReserve, -reserveMinor)]);

    /// <summary>The creator is paid what they are owed; the PSP balance pays it.</summary>
    public static Result<MoneyJournal> ForPayout(Guid campaignId, string currency, long amountMinor) =>
        Create(campaignId, JournalKinds.Payout, SplitRef(campaignId), currency,
            [new(Accounts.CreatorPayable, amountMinor), new(Accounts.PspBalance, -amountMinor)]);

    private static string SplitRef(Guid campaignId) => PublicId.Format("cmp", campaignId);
}

/// <summary>The platform's money policy, in basis points (1% is 100).</summary>
public sealed record MoneyPolicy(int PlatformFeeBasisPoints, int ReserveBasisPoints)
{
    /// <summary>A share of an amount, rounded to the nearest minor unit, half away from zero.</summary>
    public static long Share(long amountMinor, int basisPoints) =>
        (long)Math.Round(amountMinor * (decimal)basisPoints / 10_000m, MidpointRounding.AwayFromZero);

    public long PlatformFee(long collectedMinor) => Share(collectedMinor, PlatformFeeBasisPoints);

    public long Reserve(long creatorShareMinor) => Share(creatorShareMinor, ReserveBasisPoints);
}

/// <summary>Where a campaign's money stands. Each move is conditional on the one before.</summary>
public enum PayoutStatus
{
    Settled,
    PaidOut,
}

public static class PayoutRules
{
    /// <summary>The status a payout may start from.</summary>
    public const PayoutStatus PayableFrom = PayoutStatus.Settled;

    /// <summary>A status as the database and the API spell it: <c>PaidOut</c> is <c>PAID_OUT</c>.</summary>
    public static string Name(this PayoutStatus status) => status switch
    {
        PayoutStatus.Settled => "SETTLED",
        PayoutStatus.PaidOut => "PAID_OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
