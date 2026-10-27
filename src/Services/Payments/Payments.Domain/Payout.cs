namespace Payments.Domain;

/// <summary>
/// A settled campaign's money on its way to the creator: how much was paid, how much is held back,
/// and the PSP's transfer. One per campaign.
/// </summary>
public sealed class Payout
{
#pragma warning disable CS8618
    private Payout()
    {
    }
#pragma warning restore CS8618

    public Guid CampaignId { get; private set; }

    public string Currency { get; private set; }

    public PayoutStatus Status { get; private set; }

    /// <summary>What the transfer pays the creator; set when the split is posted.</summary>
    public long? AmountMinor { get; private set; }

    /// <summary>What is held back in the creator's reserve; set with the amount.</summary>
    public long? ReserveMinor { get; private set; }

    public string? PspTransferId { get; private set; }

    public DateTimeOffset SettledAt { get; private set; }

    public DateTimeOffset? ReconciledAt { get; private set; }

    public DateTimeOffset? PaidOutAt { get; private set; }
}
