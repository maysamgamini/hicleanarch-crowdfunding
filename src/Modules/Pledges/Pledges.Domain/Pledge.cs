using BuildingBlocks.Domain;

namespace Pledges.Domain;

/// <summary>A pledge's life. Only PENDING_SETUP and ACTIVE are live and count against one backer's one pledge.</summary>
public enum PledgeStatus
{
    PendingSetup,
    Active,
    Expired,
    Cancelled,
    Released,
    ToCharge,
    Charged,
    Failed,
}

/// <summary>A backer's promise to pay an amount, optionally for a tier, if the campaign is funded.</summary>
public sealed class Pledge
{
    public const string IdPrefix = "plg";

    // For the persistence layer, which sets the properties itself.
#pragma warning disable CS8618
    private Pledge()
    {
    }
#pragma warning restore CS8618

    public Guid Id { get; private set; }

    public Guid CampaignId { get; private set; }

    public Guid BackerId { get; private set; }

    public Guid? TierId { get; private set; }

    public long AmountMinor { get; private set; }

    public PledgeStatus Status { get; private set; }

    /// <summary>The PSP's reference to the saved card. Card numbers never reach us.</summary>
    public string? PaymentMethodRef { get; private set; }

    public DateTimeOffset? HoldExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string PublicId => BuildingBlocks.Domain.PublicId.Format(IdPrefix, Id);

    /// <summary>
    /// A pledge on a card the backer has already saved: it counts at once. The amount must be
    /// positive and at least the tier's minimum.
    /// </summary>
    public static Result<Pledge> WithSavedCard(Guid campaignId, Guid backerId, TierCounter? tier, long amountMinor, string? paymentMethodRef)
    {
        var errors = new List<Error>();
        if (amountMinor <= 0)
        {
            errors.Add(PledgeErrors.AmountNotPositive);
        }
        else if (tier is not null && amountMinor < tier.MinPledgeMinor)
        {
            errors.Add(PledgeErrors.BelowTierMinimum);
        }

        if (string.IsNullOrWhiteSpace(paymentMethodRef))
        {
            errors.Add(PledgeErrors.PaymentMethodRequired);
        }

        if (errors.Count > 0)
        {
            return Result.Failure<Pledge>(errors);
        }

        return Result.Success(new Pledge
        {
            Id = Guid.CreateVersion7(),
            CampaignId = campaignId,
            BackerId = backerId,
            TierId = tier?.TierId,
            AmountMinor = amountMinor,
            Status = PledgeStatus.Active,
            PaymentMethodRef = paymentMethodRef,
        });
    }
}

public enum LedgerKind
{
    Activate,
    Change,
    Cancel,
}

/// <summary>
/// One change to a promise, never edited. <see cref="CauseId"/> names what caused it: a request's
/// key, or the id of the message that said a card was saved. The total is the sum of the deltas.
/// </summary>
public sealed class LedgerEntry
{
#pragma warning disable CS8618
    private LedgerEntry()
    {
    }
#pragma warning restore CS8618

    public long EntryId { get; private set; }

    public Guid CampaignId { get; private set; }

    public Guid PledgeId { get; private set; }

    public string CauseId { get; private set; }

    public long DeltaMinor { get; private set; }

    public LedgerKind Kind { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static LedgerEntry Activate(Pledge pledge, string causeId) => new()
    {
        CampaignId = pledge.CampaignId,
        PledgeId = pledge.Id,
        CauseId = causeId,
        DeltaMinor = pledge.AmountMinor,
        Kind = LedgerKind.Activate,
    };
}
