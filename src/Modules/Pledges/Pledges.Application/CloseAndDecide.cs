using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Pledges.Application;

/// <summary>The close and the decision, as the database does them.</summary>
public interface IClosing
{
    /// <summary>
    /// Campaigns whose deadline has passed and that are still LIVE, or ENDED and undecided. With
    /// <paramref name="campaignId"/>, only that one.
    /// </summary>
    Task<IReadOnlyList<Guid>> Due(int batchSize, Guid? campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The fence's close side: LIVE to ENDED once the deadline has passed by the database's clock.
    /// False: it was not LIVE (already ended, or cancelled).
    /// </summary>
    Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// How long to wait after an ENDED commit is acknowledged before the exact sum. Zero when the
    /// close itself waited for every pledge in flight (a row lock); the transactions' time limit plus
    /// a margin when pledges take no lock. <see cref="TryDecide"/> refuses to decide sooner.
    /// </summary>
    TimeSpan WaitAfterEnd { get; }

    /// <summary>
    /// The check behind the fence: for campaigns decided at least a minute ago and not yet checked,
    /// sums the ledger again and compares it with the recorded sum. Returns how many differed; each
    /// is logged and counted for an alarm.
    /// </summary>
    Task<int> RecheckDecided(int batchSize, CancellationToken cancellationToken);

    /// <summary>The exact sum of the ledger and the number of active pledges, read after the close.</summary>
    Task<(long SumMinor, int Backers, long GoalMinor, string Currency)> ExactSum(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>ENDED to the outcome, with the final sum, once. False: someone else decided it first.</summary>
    Task<bool> TryDecide(Guid campaignId, bool funded, long finalMinor, CancellationToken cancellationToken);

    /// <summary>Every ACTIVE pledge to TO_CHARGE, returned so each can be asked for.</summary>
    Task<IReadOnlyList<ChargeablePledge>> MarkToCharge(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Every ACTIVE pledge to RELEASED. Nothing was charged, so nothing is undone at the PSP.</summary>
    Task<int> Release(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Every pledge still waiting for its card to EXPIRED, and its places given back: it never counted.</summary>
    Task<int> ExpireWaiting(Guid campaignId, CancellationToken cancellationToken);
}

public sealed record ChargeablePledge(Guid PledgeId, Guid BackerId, long AmountMinor, string PaymentMethodRef);

/// <summary>One pass of the close job. With <see cref="CampaignId"/>, only that campaign (an operator's or a test's pass).</summary>
public sealed record CloseDueCampaigns(Guid? CampaignId = null) : ICommand<int>;

/// <summary>
/// One pass of the close job. For each due campaign, two transactions: the close, and then, once
/// every pledge that saw the campaign LIVE has committed or ended, the decision from the exact sum,
/// which moves every pledge in the same transaction. A campaign left ENDED by a pass that died between the two is picked up by
/// the next pass, and both steps are conditional, so two passes at once decide a campaign once.
/// </summary>
public sealed class CloseDueCampaignsHandler(
    IClosing closing,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PledgesModuleName.Value)] ITransactionScope transaction) : ICommandHandler<CloseDueCampaigns, int>
{
    public async Task<Result<int>> Handle(CloseDueCampaigns command, CancellationToken cancellationToken)
    {
        // First end every due campaign, then wait once, from the moment the last ENDED commit was
        // acknowledged, then decide each. A campaign left ENDED by an earlier pass is decided too.
        var due = await closing.Due(50, command.CampaignId, cancellationToken);
        var ended = false;
        foreach (var campaignId in due)
        {
            var end = await transaction.Run(async ct => Result.Success(await closing.TryEnd(campaignId, ct)), cancellationToken);
            ended |= end.Value;
        }

        if (ended && closing.WaitAfterEnd > TimeSpan.Zero)
        {
            await Task.Delay(closing.WaitAfterEnd, clock, cancellationToken);
        }

        var decided = 0;
        foreach (var campaignId in due)
        {
            var outcome = await transaction.Run(ct => Decide(campaignId, ct), cancellationToken);
            if (outcome.IsSuccess && outcome.Value)
            {
                decided++;
            }
        }

        await closing.RecheckDecided(50, cancellationToken);
        return Result.Success(decided);
    }

    private async Task<Result<bool>> Decide(Guid campaignId, CancellationToken ct)
    {
        var (sum, backers, goal, currency) = await closing.ExactSum(campaignId, ct);
        var funded = sum >= goal;
        if (!await closing.TryDecide(campaignId, funded, sum, ct))
        {
            return Result.Success(false); // still LIVE, cancelled, or decided by another pass
        }

        await closing.ExpireWaiting(campaignId, ct);
        var now = clock.GetUtcNow();
        if (funded)
        {
            foreach (var pledge in await closing.MarkToCharge(campaignId, ct))
            {
                await outbox.Add(new PledgeChargeRequested(
                    Guid.CreateVersion7(), now, pledge.PledgeId, campaignId, pledge.BackerId, pledge.AmountMinor, currency, pledge.PaymentMethodRef), ct);
            }
        }
        else
        {
            await closing.Release(campaignId, ct);
        }

        await outbox.Add(new CampaignFundingDecided(
            Guid.CreateVersion7(), now, campaignId,
            funded ? CampaignFundingDecided.Funded : CampaignFundingDecided.Unsuccessful,
            sum, backers, goal), ct);
        return Result.Success(true);
    }
}
