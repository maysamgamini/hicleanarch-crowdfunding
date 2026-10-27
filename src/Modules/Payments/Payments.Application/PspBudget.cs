using BuildingBlocks.Application;

namespace Payments.Application;

/// <summary>
/// Configuration section <c>Payments:Budget</c>: the PSP's rate limits as token buckets that every
/// task shares (lesson s3-a04). The defaults are Stripe's documented live limits: 100 requests a
/// second for the account, 25 a second per endpoint. A negotiated limit only changes these numbers.
/// </summary>
public sealed class PspBudget
{
    /// <summary>Names the buckets; an environment, or a test, that shares a Valkey uses its own.</summary>
    public string Prefix { get; set; } = "psp";

    public double AccountPerSecond { get; set; } = 100;

    public double CardSetupsPerSecond { get; set; } = 25;

    /// <summary>A card setup takes a token from the account's bucket and from the card-setup lane's.</summary>
    public IReadOnlyList<Bucket> CardSetup =>
    [
        new($"{Prefix}:account", new BucketRate(AccountPerSecond, Capacity(AccountPerSecond))),
        new($"{Prefix}:card-setups", new BucketRate(CardSetupsPerSecond, Capacity(CardSetupsPerSecond))),
    ];

    /// <summary>A bucket holds one second of its rate, and at least one token.</summary>
    private static int Capacity(double perSecond) => Math.Max(1, (int)Math.Floor(perSecond));
}
