namespace Payments.Application;

/// <summary>
/// The PSP, in this design's own words. Adapters implement it for one provider; the course's fake
/// behaves like a real one where the design depends on it. A call that does not reach the PSP, or
/// that the adapter does not send because calls are paused, throws <see cref="PspUnavailableException"/>,
/// whatever the adapter's transport.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>The backer as the PSP's customer. The same key returns the same customer (Stripe keeps a key's reply for 24 hours).</summary>
    Task<string> CreateCustomer(Guid backerId, string idempotencyKey, CancellationToken ct);

    /// <summary>A card form for one pledge. The same key returns the same setup and client secret, so a retry starts nothing new.</summary>
    Task<CardSetupIntent> StartCardSetup(string pspCustomerId, string idempotencyKey, CancellationToken ct);

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

    /// <summary>Gives a charge back to the backer. The same key returns the same refund; the PSP keeps its fee.</summary>
    Task<string> Refund(string pspPaymentId, long amountMinor, string idempotencyKey, CancellationToken ct);

    /// <summary>Pays a creator from the platform's PSP balance. The same key returns the same transfer.</summary>
    Task<string> Transfer(Guid creatorId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// The PSP's own record of money moved in a period: each charge and refund, with its fee and the
    /// pledge in its metadata (at Stripe, the balance transactions report).
    /// </summary>
    Task<BalanceReport> BalanceTransactions(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

/// <summary>
/// The port's failure: the PSP could not be reached, or calls to it are paused. Adapters translate
/// their transport's own errors into it, so a use case never handles HTTP. Nothing is known to have
/// happened at the PSP; a retry with the same idempotency key is safe.
/// </summary>
public class PspUnavailableException : Exception
{
    public PspUnavailableException()
        : base("The PSP could not be reached.")
    {
    }

    public PspUnavailableException(string message)
        : base(message)
    {
    }

    public PspUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record OnSessionPayment(string PspPaymentId, string ClientSecret);

/// <summary>
/// The PSP's balance-transactions report for a period. A PSP builds it a while after the money
/// moves; <see cref="CoveredUntil"/> is how far it is complete, and nothing after it can be trusted
/// to be in it yet.
/// </summary>
public sealed record BalanceReport(IReadOnlyList<BalanceTransaction> Transactions, DateTimeOffset CoveredUntil);

/// <summary>One line of the PSP's report. A refund's amount is negative; <see cref="SourceId"/> is the payment or refund.</summary>
public sealed record BalanceTransaction(string Id, string SourceId, string PledgeId, long AmountMinor, long FeeMinor, string Currency, DateTimeOffset CreatedAt);

public sealed record CardSetupIntent(string PspSetupId, string ClientSecret);

public sealed record ChargeRequest(string PspCustomerId, string PaymentMethodRef, long AmountMinor, string Currency, string PledgeId, int AttemptNo);

public sealed record ChargeReference(string? PspPaymentId, string PspCustomerId, string PledgeId, int AttemptNo);

/// <summary>What we know about one charge attempt. <see cref="Unknown"/> is an answer of its own: never a failure.</summary>
public abstract record ChargeOutcome
{
    /// <summary>The PSP took its fee from the charge (at Stripe, on the charge's balance transaction); the ledger records it.</summary>
    /// <summary>FeeMinor is null when the message did not carry the fee (a webhook without it); the journal is then booked before payout.</summary>
    public sealed record Succeeded(string PspPaymentId, long? FeeMinor = null) : ChargeOutcome;

    /// <summary>
    /// Declined by the bank. <see cref="AdviceCode"/> is the PSP's advice on trying again
    /// (Stripe's <c>advice_code</c>: <c>do_not_try_again</c>, <c>try_again_later</c>, <c>confirm_card_data</c>), when it gives one.
    /// </summary>
    public sealed record Declined(string Code, string? PspPaymentId, string? AdviceCode = null) : ChargeOutcome;

    public sealed record Unknown(string Reason) : ChargeOutcome;

    /// <summary>The reason a payment that waits for the backer gives while it waits.</summary>
    public const string WaitingForBacker = "requires_action";

    /// <summary>A lookup found no payment for the attempt: the first request never reached the PSP.</summary>
    public sealed record NotFound : ChargeOutcome;
}
