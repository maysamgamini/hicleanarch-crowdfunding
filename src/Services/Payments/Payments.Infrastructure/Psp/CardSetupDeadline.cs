using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Contracts;
using Payments.Domain;

namespace Payments.Infrastructure.Psp;

/// <summary>Configuration section <c>Payments:CardSetup</c>.</summary>
public sealed class CardSetupOptions
{
    /// <summary>
    /// The longest a card setup may take, waits for budget tokens included. A backer's request waits
    /// for it, so it is shorter than any caller's own timeout (from Stage 4, the remote client's 2 s
    /// attempt): a slow PSP is answered here, as 409, instead of timing the caller out.
    /// </summary>
    public TimeSpan PspDeadline { get; set; } = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// A card setup with a deadline: the customer, the setup and the budget waits before them share
/// it. When it passes, the setup is abandoned and the backer gets <see cref="PaymentErrors.PspUnavailable"/>
/// (409, try again). A setup the PSP made but we never recorded costs nothing: the retry sends the
/// pledge's same key and gets that setup back. Settlement has its own, longer, PSP timeout.
/// </summary>
internal sealed class CardSetupDeadline(ICommandHandler<StartCardSetup, CardSetupStarted> inner, CardSetupOptions options, TimeProvider clock)
    : ICommandHandler<StartCardSetup, CardSetupStarted>
{
    public async Task<Result<CardSetupStarted>> Handle(StartCardSetup command, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(options.PspDeadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await inner.Handle(command, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<CardSetupStarted>(PaymentErrors.PspUnavailable);
        }
    }
}
