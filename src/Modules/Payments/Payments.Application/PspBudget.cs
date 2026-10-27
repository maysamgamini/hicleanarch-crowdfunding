using BuildingBlocks.Application;

namespace Payments.Application;

/// <summary>The PSP's endpoints that have a rate limit of their own.</summary>
public enum PspCall
{
    Customer,
    CardSetup,
    Charge,
    LookUp,
}

/// <summary>
/// Configuration section <c>Payments:Budget</c>: the PSP's rate limits as token buckets that every
/// task shares (lesson s3-a04). Every PSP call takes a token from the account's bucket and from its
/// endpoint's. The defaults are Stripe's documented live limits: 100 requests a second for the
/// account, 25 a second per endpoint. A negotiated limit only changes these numbers.
/// </summary>
public sealed class PspBudget
{
    /// <summary>Names the buckets; an environment, or a test, that shares a Valkey uses its own.</summary>
    public string Prefix { get; set; } = "psp";

    public double AccountPerSecond { get; set; } = 100;

    public double CustomersPerSecond { get; set; } = 25;

    public double CardSetupsPerSecond { get; set; } = 25;

    public double ChargesPerSecond { get; set; } = 25;

    public double LookUpsPerSecond { get; set; } = 25;

    public Bucket Account => Named("account", AccountPerSecond);

    public Bucket Endpoint(PspCall call) => call switch
    {
        PspCall.Customer => Named("customers", CustomersPerSecond),
        PspCall.CardSetup => Named("card-setups", CardSetupsPerSecond),
        PspCall.Charge => Named("charges", ChargesPerSecond),
        _ => Named("lookups", LookUpsPerSecond),
    };

    /// <summary>The buckets one call takes a token from: the account's and its endpoint's.</summary>
    public IReadOnlyList<Bucket> For(PspCall call) => [Account, Endpoint(call)];

    /// <summary>A card setup's turn in the card-setup lane.</summary>
    public IReadOnlyList<Bucket> CardSetup => For(PspCall.CardSetup);

    private Bucket Named(string name, double perSecond) =>
        new($"{Prefix}:{name}", new BucketRate(perSecond, Math.Max(1, (int)Math.Floor(perSecond)))); // a second's worth, at least one
}

/// <summary>
/// The PSP refused a call for its rate limit (HTTP 429). <see cref="Global"/>: the account's limit
/// was hit (Stripe's <c>Stripe-Rate-Limited-Reason</c> starts with <c>global</c>); otherwise the endpoint's.
/// </summary>
public sealed class PspRateLimitedException : Exception
{
    public PspRateLimitedException()
    {
    }

    public PspRateLimitedException(string message)
        : base(message)
    {
    }

    public PspRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PspRateLimitedException(bool global)
        : base(global ? "The PSP's account limit was hit." : "The PSP's endpoint limit was hit.") => Global = global;

    public bool Global { get; }
}
