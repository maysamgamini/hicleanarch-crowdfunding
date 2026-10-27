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

    /// <summary>
    /// A payment the backer confirms on our page, with the bank's check: the PSP creates it and the
    /// backer's browser confirms it with the secret. Its result arrives as a webhook. The same key
    /// returns the same payment.
    /// </summary>
    Task<OnSessionPayment> StartOnSessionPayment(ChargeRequest request, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Ends a payment that is still waiting for the backer. If the backer confirmed it meanwhile, the
    /// answer is its success: a payment that went through is never treated as failed.
    /// </summary>
    Task<ChargeOutcome> Cancel(ChargeReference reference, CancellationToken ct);
}

public sealed record OnSessionPayment(string PspPaymentId, string ClientSecret);

public sealed record CardSetupIntent(string PspSetupId, string ClientSecret);

public sealed record ChargeRequest(string PspCustomerId, string PaymentMethodRef, long AmountMinor, string Currency, string PledgeId, int AttemptNo);

public sealed record ChargeReference(string? PspPaymentId, string PspCustomerId, string PledgeId, int AttemptNo);

/// <summary>What we know about one charge attempt. <see cref="Unknown"/> is an answer of its own: never a failure.</summary>
public abstract record ChargeOutcome
{
    public sealed record Succeeded(string PspPaymentId) : ChargeOutcome;

    public sealed record Declined(string Code, string? PspPaymentId) : ChargeOutcome;

    public sealed record Unknown(string Reason) : ChargeOutcome;

    /// <summary>The reason a payment that waits for the backer gives while it waits.</summary>
    public const string WaitingForBacker = "requires_action";

    /// <summary>A lookup found no payment for the attempt: the first request never reached the PSP.</summary>
    public sealed record NotFound : ChargeOutcome;
}
