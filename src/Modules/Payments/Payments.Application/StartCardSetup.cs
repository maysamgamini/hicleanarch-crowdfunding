using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Contracts;
using Payments.Domain;

namespace Payments.Application;

/// <summary>
/// Finds or creates the backer's customer at the PSP, starts a card setup and records it. Each step
/// commits on its own: a setup left behind by a pledge that rolled back is never completed by a
/// backer, and a completed one for a pledge that no longer waits activates nothing. A setup first
/// takes its turn in the card-setup lane (lesson s3-a04); a full lane answers 429 with the wait.
/// </summary>
public sealed class StartCardSetupHandler(IPaymentProvider psp, IPspCustomers customers, ICardSetups setups, ITokenBuckets buckets, PspBudget budget)
    : ICommandHandler<StartCardSetup, CardSetupStarted>
{
    public async Task<Result<CardSetupStarted>> Handle(StartCardSetup command, CancellationToken cancellationToken)
    {
        var turn = await buckets.TryTake(budget.CardSetup, cancellationToken);
        if (!turn.Granted)
        {
            return Result.Failure<CardSetupStarted>(PaymentErrors.CardSetupLaneFull(turn.RetryAfter));
        }

        try
        {
            var customer = await customers.Find(command.BackerId, cancellationToken)
                ?? await customers.Add(PspCustomer.For(command.BackerId, await psp.CreateCustomer(command.BackerId, cancellationToken)), cancellationToken);
            var intent = await psp.StartCardSetup(customer, cancellationToken);
            var setup = CardSetup.Start(command.PledgeId, command.BackerId, intent.PspSetupId);
            await setups.Add(setup, cancellationToken);
            return Result.Success(new CardSetupStarted(setup.Id, intent.ClientSecret));
        }
        catch (HttpRequestException)
        {
            return Result.Failure<CardSetupStarted>(PaymentErrors.PspUnavailable);
        }
    }
}
