namespace Payments.Domain;

public enum CardSetupStatus
{
    Started,
    Succeeded,
    Failed,
}

/// <summary>One attempt to save a backer's card at the PSP, for one pledge.</summary>
public sealed class CardSetup
{
    // For the persistence layer, which sets the properties itself.
#pragma warning disable CS8618
    private CardSetup()
    {
    }
#pragma warning restore CS8618

    public Guid Id { get; private set; }

    public Guid PledgeId { get; private set; }

    public Guid BackerId { get; private set; }

    /// <summary>The PSP's id for this setup (a SetupIntent at Stripe).</summary>
    public string PspSetupId { get; private set; }

    public CardSetupStatus Status { get; private set; }

    public string? PaymentMethodRef { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static CardSetup Start(Guid pledgeId, Guid backerId, string pspSetupId) => new()
    {
        Id = Guid.CreateVersion7(),
        PledgeId = pledgeId,
        BackerId = backerId,
        PspSetupId = pspSetupId,
        Status = CardSetupStatus.Started,
    };
}

/// <summary>A backer as the PSP knows them. One per backer, created the first time it is needed.</summary>
public sealed class PspCustomer
{
#pragma warning disable CS8618
    private PspCustomer()
    {
    }
#pragma warning restore CS8618

    public Guid BackerId { get; private set; }

    public string PspCustomerId { get; private set; }

    public static PspCustomer For(Guid backerId, string pspCustomerId) => new() { BackerId = backerId, PspCustomerId = pspCustomerId };
}
