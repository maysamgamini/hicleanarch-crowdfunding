using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Domain;

namespace Payments.Application;

/// <summary>A charge as its backer sees it in the recovery window.</summary>
public sealed record ChargeView(string PledgeId, string Status, DateTimeOffset? NextAttemptAt);

/// <summary>What the backer's browser needs to confirm a payment with the bank's check.</summary>
public sealed record PaymentToConfirm(string PledgeId, string PspPaymentId, string ClientSecret);

/// <summary>A hard decline: the backer gives a new saved card, and it is tried at once (lesson s3-a04).</summary>
public sealed record UseNewCard(Guid PledgeId, Guid BackerId, string PaymentMethodRef) : ICommand<ChargeView>;

/// <summary>The bank wanted the cardholder: the backer confirms the payment on our page (lesson s3-a04).</summary>
public sealed record ConfirmPayment(Guid PledgeId, Guid BackerId) : ICommand<PaymentToConfirm>;

public sealed class UseNewCardHandler(ISettlements settlements, TimeProvider clock) : ICommandHandler<UseNewCard, ChargeView>
{
    public async Task<Result<ChargeView>> Handle(UseNewCard command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.PaymentMethodRef))
        {
            return Result.Failure<ChargeView>(PaymentErrors.PaymentMethodRequired);
        }

        var charge = await settlements.FindForBacker(command.PledgeId, command.BackerId, cancellationToken);
        if (charge is null)
        {
            return Result.Failure<ChargeView>(PaymentErrors.ChargeNotFound);
        }

        if (!await settlements.TryUseNewCard(command.PledgeId, command.PaymentMethodRef, cancellationToken))
        {
            return Result.Failure<ChargeView>(PaymentErrors.NotAwaitingBacker);
        }

        return Result.Success(new ChargeView(charge.PledgePublicId, "PAYMENT_FAILED", clock.GetUtcNow()));
    }
}

/// <summary>
/// Starts the next attempt as a payment the backer confirms in the browser. The attempt is recorded
/// before the PSP is called, as every attempt is; its result arrives as the PSP's webhook, and the
/// resolver cancels it if the window ends first.
/// </summary>
public sealed class ConfirmPaymentHandler(ISettlements settlements, IPaymentProvider psp) : ICommandHandler<ConfirmPayment, PaymentToConfirm>
{
    public async Task<Result<PaymentToConfirm>> Handle(ConfirmPayment command, CancellationToken cancellationToken)
    {
        var charge = await settlements.FindForBacker(command.PledgeId, command.BackerId, cancellationToken);
        if (charge is null)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.ChargeNotFound);
        }

        if (charge.Status != ChargeStatus.PaymentFailed || DunningPolicy.Classify(charge.LastDeclineCode ?? "", charge.LastAdviceCode) != DeclineKind.AuthenticationRequired)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.NotAwaitingBacker);
        }

        var attemptNo = await settlements.TryStartAttempt(charge.PledgeId, charge.PledgePublicId, onSession: true, cancellationToken);
        if (attemptNo is not { } n)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.NotAwaitingBacker);
        }

        try
        {
            var customer = await settlements.CustomerOf(charge.BackerId, cancellationToken);
            var payment = await psp.StartOnSessionPayment(
                new ChargeRequest(customer, charge.PaymentMethodRef, charge.AmountMinor, charge.Currency, charge.PledgePublicId, n),
                Charge.KeyFor(charge.PledgePublicId, n),
                cancellationToken);
            await settlements.RecordPayment(charge.PledgeId, n, payment.PspPaymentId, cancellationToken);
            return Result.Success(new PaymentToConfirm(charge.PledgePublicId, payment.PspPaymentId, payment.ClientSecret));
        }
        catch (HttpRequestException)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.PspUnavailable);
        }
    }
}
