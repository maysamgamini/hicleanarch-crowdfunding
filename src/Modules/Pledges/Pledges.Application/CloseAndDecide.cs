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
    /// It waits for every pledge that holds the window's lock. False: it was not LIVE (already ended,
    /// or cancelled).
    /// </summary>
    Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken);

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
/// One pass of the close job. For each due campaign, two transactions: the close, which waits for
/// every pledge in flight, and then the decision from the exact sum, which moves every pledge in
/// the same transaction. A campaign left ENDED by a pass that died between the two is picked up by
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
        var decided = 0;
        foreach (var campaignId in await closing.Due(50, command.CampaignId, cancellationToken))
        {
            await transaction.Run(async ct => Result.Success(await closing.TryEnd(campaignId, ct)), cancellationToken);
            var outcome = await transaction.Run(ct => Decide(campaignId, ct), cancellationToken);
            if (outcome.IsSuccess && outcome.Value)
            {
                decided++;
            }
        }

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
