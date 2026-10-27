using Payments.Domain;

namespace Payments.Application;

public interface IPspCustomers
{
    Task<string?> Find(Guid backerId, CancellationToken cancellationToken);

    /// <summary>Keeps the first customer recorded for a backer; returns the one that is kept.</summary>
    Task<string> Add(PspCustomer customer, CancellationToken cancellationToken);
}

/// <summary>
/// The idempotency keys Payments sends the PSP for calls that create something: one customer per
/// backer and one card setup per pledge, however often the call is repeated.
/// </summary>
public static class PspKeys
{
    public static string Customer(Guid backerId) => $"customer:{backerId}";

    public static string CardSetup(Guid pledgeId) => $"card-setup:{pledgeId}";
}

public static class PspCustomersExtensions
{
    /// <summary>
    /// The backer's customer at the PSP, created the first time. Two first calls that race send the
    /// same key, so the PSP answers both with one customer, and the first row recorded is kept.
    /// </summary>
    public static async Task<string> FindOrCreate(this IPspCustomers customers, IPaymentProvider psp, Guid backerId, CancellationToken cancellationToken) =>
        await customers.Find(backerId, cancellationToken)
            ?? await customers.Add(PspCustomer.For(backerId, await psp.CreateCustomer(backerId, PspKeys.Customer(backerId), cancellationToken)), cancellationToken);
}

public interface ICardSetups
{
    /// <summary>
    /// Records the pledge's one setup, or keeps the one already recorded for it; returns the row that
    /// is kept. A kept setup still STARTED takes the PSP's setup id given here, so the form whose
    /// secret the backer holds is the one recorded.
    /// </summary>
    Task<CardSetup> Add(CardSetup setup, CancellationToken cancellationToken);

    /// <summary>STARTED to SUCCEEDED, once. Null: unknown setup, or it had already moved.</summary>
    Task<CardSetup?> TryComplete(string pspSetupId, string paymentMethodRef, CancellationToken cancellationToken);

    /// <summary>STARTED to FAILED, once.</summary>
    Task<bool> TryFail(string pspSetupId, CancellationToken cancellationToken);
}

/// <summary>The PSP's messages we have seen. A message id is recorded once; a repeat changes nothing.</summary>
public interface IWebhookInbox
{
    Task<bool> TryRecord(string messageId, CancellationToken cancellationToken);
}
