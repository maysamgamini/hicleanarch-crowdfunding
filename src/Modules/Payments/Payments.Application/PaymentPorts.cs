using Payments.Domain;

namespace Payments.Application;

public interface IPspCustomers
{
    Task<string?> Find(Guid backerId, CancellationToken cancellationToken);

    /// <summary>Keeps the first customer recorded for a backer; returns the one that is kept.</summary>
    Task<string> Add(PspCustomer customer, CancellationToken cancellationToken);
}

public interface ICardSetups
{
    Task Add(CardSetup setup, CancellationToken cancellationToken);

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
