using BuildingBlocks.Application;

namespace Payments.Contracts;

/// <summary>
/// Asks Payments to start saving a backer's card for a pledge that is waiting for one. Payments
/// talks to the PSP and answers with the secret the PSP's card form needs; the card itself goes
/// from the backer's browser to the PSP, never through us. Once Payments runs as a service of its own,
/// the command travels over HTTP to its internal endpoint.
/// </summary>
[RemoteEndpoint("POST", "/internal/payments/card-setups")]
public sealed record StartCardSetup(Guid PledgeId, Guid BackerId) : ICommand<CardSetupStarted>;

public sealed record CardSetupStarted(Guid CardSetupId, string ClientSecret);

/// <summary>
/// Payments' endpoints for other services (from s4-l03), on its internal port, which no load balancer
/// forwards. Each command's own <see cref="RemoteEndpointAttribute"/> names its method and path (s4-l06).
/// </summary>
public static class PaymentsInternalEndpoints
{
    /// <summary>The scope a caller's service token needs for Payments' commands (s4-l04).</summary>
    public const string CommandsScope = "payments/commands";
}

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
