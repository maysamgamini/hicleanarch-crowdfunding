using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Domain;

namespace Payments.Application;

/// <summary>A charge as its backer sees it in the recovery window.</summary>
public sealed record ChargeView(string PledgeId, string Status, DateTimeOffset? NextAttemptAt);

/// <summary>What the backer's browser needs to confirm a payment with the bank's check.</summary>
public sealed record PaymentToConfirm(string PledgeId, string PspPaymentId, string ClientSecret);

/// <summary>
/// A hard decline: the backer gives a new saved card, and it is tried at once (lesson s3-a04). A
/// decline that asks for the cardholder's confirmation is refused with <c>charge_needs_confirmation</c>:
/// the backer confirms the payment instead.
/// </summary>
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

        // The bank wants the cardholder to confirm, not another card: a new one would meet the same check.
        if (charge.Status == ChargeStatus.PaymentFailed && DunningPolicy.Classify(charge.LastDeclineCode ?? "", charge.LastAdviceCode) == DeclineKind.AuthenticationRequired)
        {
            return Result.Failure<ChargeView>(PaymentErrors.NeedsConfirmationNotCard);
        }

        if (!await settlements.TryUseNewCard(command.PledgeId, command.PaymentMethodRef, cancellationToken))
        {
            return Result.Failure<ChargeView>(PaymentErrors.NotAwaitingBacker);
        }

        return Result.Success(new ChargeView(charge.PledgePublicId, "PAYMENT_FAILED", clock.GetUtcNow()));
    }
}

/// <summary>
/// Starts the next attempt as a payment the backer confirms in the browser. The attempt is committed
/// before the PSP is called, as every attempt is, so the endpoint runs this before its Idempotency-Key
/// transaction and stores only the reply. Its result arrives as the PSP's webhook; nobody looks it up
/// before the window ends, when the resolver cancels it if it is still waiting. A retry while the
/// attempt waits sends the same key again, and the PSP answers with the same payment.
/// </summary>
public sealed class ConfirmPaymentHandler(ISettlements settlements, IPaymentProvider psp, DunningPolicy dunning, TimeProvider clock) : ICommandHandler<ConfirmPayment, PaymentToConfirm>
{
    public async Task<Result<PaymentToConfirm>> Handle(ConfirmPayment command, CancellationToken cancellationToken)
    {
        var charge = await settlements.FindForBacker(command.PledgeId, command.BackerId, cancellationToken);
        if (charge is null)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.ChargeNotFound);
        }

        int n;
        if (charge.Status == ChargeStatus.Charging && charge.OnSession)
        {
            n = charge.AttemptNo;
        }
        else if (charge.Status == ChargeStatus.PaymentFailed && DunningPolicy.Classify(charge.LastDeclineCode ?? "", charge.LastAdviceCode) == DeclineKind.AuthenticationRequired)
        {
            var windowEnds = (charge.PaymentFailedAt ?? clock.GetUtcNow()) + dunning.Window;
            if (await settlements.TryStartAttempt(charge.PledgeId, charge.PledgePublicId, onSession: true, lookUpAt: windowEnds, cancellationToken) is not { } started)
            {
                return Result.Failure<PaymentToConfirm>(PaymentErrors.NotAwaitingBacker);
            }

            n = started;
        }
        else
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
        catch (PspUnavailableException)
        {
            return Result.Failure<PaymentToConfirm>(PaymentErrors.PspUnavailable);
        }
    }
}
