using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Payments.Contracts;
using Payments.Domain;

namespace Payments.Application;

/// <summary>
/// Finds or creates the backer's customer at the PSP, starts the pledge's card setup and records it.
/// Each step commits on its own and is idempotent: the PSP calls carry the backer's and the pledge's
/// keys, and a pledge has one setup row, so the same command sent twice, or twice at once, ends with
/// one customer, one setup and the same client secret. A setup left behind by a pledge that rolled
/// back is never completed by a backer, and a completed one for a pledge that no longer waits
/// activates nothing. A setup first takes its turn in the card-setup lane (lesson s3-a04); a full
/// lane gives the pledge a place in line and answers 429 with the wait until its turn.
/// </summary>
public sealed class StartCardSetupHandler(IPaymentProvider psp, IPspCustomers customers, ICardSetups setups, ITokenBuckets buckets, PspBudget budget)
    : ICommandHandler<StartCardSetup, CardSetupStarted>
{
    public async Task<Result<CardSetupStarted>> Handle(StartCardSetup command, CancellationToken cancellationToken)
    {
        var turn = await buckets.TryEnter(budget.CardSetupLane, command.PledgeId.ToString("N"), cancellationToken);
        if (!turn.Granted)
        {
            return Result.Failure<CardSetupStarted>(PaymentErrors.CardSetupLaneFull(turn.RetryAfter));
        }

        try
        {
            var customer = await customers.FindOrCreate(psp, command.BackerId, cancellationToken);
            var intent = await psp.StartCardSetup(customer, PspKeys.CardSetup(command.PledgeId), cancellationToken);
            var kept = await setups.Add(CardSetup.Start(command.PledgeId, command.BackerId, intent.PspSetupId), cancellationToken);
            return Result.Success(new CardSetupStarted(kept.Id, intent.ClientSecret));
        }
        catch (HttpRequestException)
        {
            return Result.Failure<CardSetupStarted>(PaymentErrors.PspUnavailable);
        }
    }
}
