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
/// Configuration section <c>Payments:Budget</c>: the PSP's limits, shared by every task (lesson
/// s3-a04). Every PSP call takes a token from the account's bucket and from its endpoint's, and a
/// slot of the account's calls in flight for as long as it runs. The defaults are Stripe's
/// documented live limits: 100 requests a second for the account, 25 a second per endpoint, 50
/// requests in progress at once. A negotiated limit only changes these numbers, and must cover
/// both: at 250 charges a second and half a second a call, 125 are in flight.
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

    /// <summary>The most PSP calls in progress at once, across every task.</summary>
    public int InFlight { get; set; } = 50;

    /// <summary>
    /// The longest a call holds its slot: longer than any PSP call's own timeout (settlement's is 20 s),
    /// so only a task that died loses its slots to the lease, and they come back when it ends.
    /// </summary>
    public TimeSpan InFlightLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The account's calls in flight.</summary>
    public SlotPool Calls => new($"{Prefix}:in-flight", InFlight, InFlightLease);

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

    /// <summary>
    /// How long a pledge keeps its place in the card-setup lane: longer than a pledge's 15-minute hold,
    /// so a backer who waits keeps it for as long as the pledge waits.
    /// </summary>
    public TimeSpan CardSetupPlaceKeptFor { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The card-setup lane (lesson s3-a04): pledges wait for the card-setup bucket's tokens in line, in
    /// the order they first asked, each told when its turn comes; at its turn it also takes the account's token.
    /// </summary>
    public Lane CardSetupLane => new(Endpoint(PspCall.CardSetup), [Account], CardSetupPlaceKeptFor);

    private Bucket Named(string name, double perSecond) =>
        new($"{Prefix}:{name}", new BucketRate(perSecond, Math.Max(1, (int)Math.Floor(perSecond)))); // a second's worth, at least one
}

/// <summary>
/// The PSP refused a call for its rate limit (HTTP 429). <see cref="Global"/>: the account's limit
/// was hit (Stripe's <c>Stripe-Rate-Limited-Reason</c> starts with <c>global</c>); otherwise the endpoint's.
/// <see cref="Concurrency"/>: the account had too many requests in progress (<c>global-concurrency</c>), not too many a second.
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

    private PspRateLimitedException(bool global, bool concurrency)
        : base("The PSP's limit on requests in progress was hit.") => (Global, Concurrency) = (global, concurrency);

    public bool Global { get; }

    public bool Concurrency { get; }

    /// <summary>Stripe's <c>global-concurrency</c>: too many requests in progress for the account.</summary>
    public static PspRateLimitedException TooManyInFlight() => new(global: true, concurrency: true);
}
