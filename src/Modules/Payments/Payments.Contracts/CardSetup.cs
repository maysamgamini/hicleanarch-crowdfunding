using BuildingBlocks.Application;

namespace Payments.Contracts;

/// <summary>
/// Asks Payments to start saving a backer's card for a pledge that is waiting for one. Payments
/// talks to the PSP and answers with the secret the PSP's card form needs; the card itself goes
/// from the backer's browser to the PSP, never through us.
/// </summary>
public sealed record StartCardSetup(Guid PledgeId, Guid BackerId) : ICommand<CardSetupStarted>;

public sealed record CardSetupStarted(Guid CardSetupId, string ClientSecret);

/// <summary>
/// The PSP says a card was saved for a pledge. <see cref="PspMessageId"/> is the id of the PSP's
/// message that said so; Pledges names it as the cause of the pledge's ledger entry.
/// </summary>
public sealed record PaymentMethodSaved(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid CardSetupId,
    Guid PledgeId,
    Guid BackerId,
    string PaymentMethodRef,
    string PspMessageId) : IntegrationEvent(EventId, OccurredAt);

/// <summary>The module's name, which keys its outbox and names its handlers.</summary>
public static class PaymentsModuleName
{
    public const string Value = "Payments";
}
