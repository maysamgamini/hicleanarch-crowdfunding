namespace Payments.Application;

/// <summary>
/// The PSP, in this design's own words. Adapters implement it for one provider; the course's fake
/// behaves like a real one where the design depends on it.
/// </summary>
public interface IPaymentProvider
{
    Task<string> CreateCustomer(Guid backerId, CancellationToken ct);

    Task<CardSetupIntent> StartCardSetup(string pspCustomerId, CancellationToken ct);

    /// <summary>An off-session charge of a saved card. The PSP stores the first result for a key and returns it to every retry.</summary>
    Task<ChargeOutcome> Charge(ChargeRequest request, string idempotencyKey, CancellationToken ct);

    /// <summary>What became of an attempt whose reply never arrived: by the PSP's payment id if we have one, else by the pledge and attempt in the payment's metadata.</summary>
    Task<ChargeOutcome> LookUp(ChargeReference reference, CancellationToken ct);
}

public sealed record CardSetupIntent(string PspSetupId, string ClientSecret);

public sealed record ChargeRequest(string PspCustomerId, string PaymentMethodRef, long AmountMinor, string Currency, string PledgeId, int AttemptNo);

public sealed record ChargeReference(string? PspPaymentId, string PspCustomerId, string PledgeId, int AttemptNo);

/// <summary>What we know about one charge attempt. <see cref="Unknown"/> is an answer of its own: never a failure.</summary>
public abstract record ChargeOutcome
{
    public sealed record Succeeded(string PspPaymentId) : ChargeOutcome;

    public sealed record Declined(string Code, string? PspPaymentId) : ChargeOutcome;

    public sealed record Unknown(string Reason) : ChargeOutcome;

    /// <summary>A lookup found no payment for the attempt: the first request never reached the PSP.</summary>
    public sealed record NotFound : ChargeOutcome;
}
