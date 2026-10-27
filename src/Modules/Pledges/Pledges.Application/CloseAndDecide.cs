using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Pledges.Application;

/// <summary>The close and the decision, as the database does them.</summary>
public interface IClosing
{
    /// <summary>
    /// Campaigns whose deadline has passed and that are still LIVE, or ENDED and undecided, and funded
    /// campaigns whose marking is unfinished. With <paramref name="campaignId"/>, only that one.
    /// </summary>
    Task<IReadOnlyList<Guid>> Due(int batchSize, Guid? campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The fence's close side: LIVE to ENDED once the deadline has passed by the database's clock.
    /// False: it was not LIVE (already ended, or cancelled).
    /// </summary>
    Task<bool> TryEnd(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// How long until every one of these campaigns that is ENDED has been ENDED for the fence's wait,
    /// whoever ended it: this pass, or another task's. Zero when none needs waiting for.
    /// </summary>
    Task<TimeSpan> StillToWait(IReadOnlyList<Guid> campaignIds, CancellationToken cancellationToken);

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

    /// <summary>
    /// ENDED to the outcome, with the final sum, once. An unsuccessful campaign's marking is finished
    /// with it; a funded one's starts after it. False: someone else decided it first.
    /// </summary>
    Task<bool> TryDecide(Guid campaignId, bool funded, long finalMinor, CancellationToken cancellationToken);

    /// <summary>
    /// The next batch of a funded campaign's ACTIVE pledges to TO_CHARGE, in pledge id order after the
    /// window's cursor, which moves to the batch's last pledge. A batch smaller than
    /// <paramref name="batchSize"/> finishes the marking. Empty: nothing left, or not a funded campaign
    /// with its marking unfinished.
    /// </summary>
    Task<IReadOnlyList<ChargeablePledge>> MarkNextBatch(Guid campaignId, int batchSize, CancellationToken cancellationToken);

    /// <summary>Every ACTIVE pledge to RELEASED. Nothing was charged, so nothing is undone at the PSP.</summary>
    Task<int> Release(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>Every pledge still waiting for its card to EXPIRED, and its places given back: it never counted.</summary>
    Task<int> ExpireWaiting(Guid campaignId, CancellationToken cancellationToken);
}

public sealed record ChargeablePledge(Guid PledgeId, Guid BackerId, long AmountMinor, string PaymentMethodRef, string Currency);

/// <summary>One pass of the close job. With <see cref="CampaignId"/>, only that campaign (an operator's or a test's pass).</summary>
public sealed record CloseDueCampaigns(Guid? CampaignId = null) : ICommand<int>;

/// <summary>Configuration section <c>Pledges</c>: how a funded campaign's pledges are marked TO_CHARGE.</summary>
public sealed class MarkingOptions
{
    /// <summary>Pledges marked per transaction: a million in one would hold their locks for minutes.</summary>
    public int MarkBatchSize { get; set; } = 5_000;

    /// <summary>Batches one pass of the close job marks before it moves on; the next pass continues from the cursor.</summary>
    public int MarkBatchesPerPass { get; set; } = 200;
}

/// <summary>
/// One pass of the close job. For each due campaign: the close; then, once every pledge that saw the
/// campaign LIVE has committed or ended (the time fence, lesson s3-l05), the decision from the exact
/// sum; then, for a funded campaign, its pledges marked TO_CHARGE in batches, each its own transaction
/// with its events and the window's cursor (lesson s3-a04). A campaign left ENDED, or funded with its
/// marking unfinished, by a pass that died is picked up by the next pass. Every step is conditional,
/// so two passes at once decide and mark a campaign once.
/// </summary>
public sealed class CloseDueCampaignsHandler(
    IClosing closing,
    MarkingOptions marking,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PledgesModuleName.Value)] ITransactionScope transaction) : ICommandHandler<CloseDueCampaigns, int>
{
    public async Task<Result<int>> Handle(CloseDueCampaigns command, CancellationToken cancellationToken)
    {
        // First end every due campaign, then wait once, until the last ENDED commit, this pass's or
        // another task's, is the fence's wait behind, then decide each. A campaign left ENDED by an
        // earlier pass is decided too.
        var due = await closing.Due(50, command.CampaignId, cancellationToken);
        foreach (var campaignId in due)
        {
            await transaction.Run(async ct => Result.Success(await closing.TryEnd(campaignId, ct)), cancellationToken);
        }

        // The guard in TryDecide judges the wait by the database's clock, so the wait is measured there
        // too, until the database says it is over; this task's clock may run a little apart from it.
        for (var wait = await closing.StillToWait(due, cancellationToken); wait > TimeSpan.Zero; wait = await closing.StillToWait(due, cancellationToken))
        {
            await Task.Delay(wait, clock, cancellationToken);
        }

        var decided = 0;
        foreach (var campaignId in due)
        {
            var outcome = await transaction.Run(ct => Decide(campaignId, ct), cancellationToken);
            if (outcome.IsSuccess && outcome.Value)
            {
                decided++;
            }

            await MarkToCharge(campaignId, cancellationToken);
        }

        await closing.RecheckDecided(50, cancellationToken);
        return Result.Success(decided);
    }

    /// <summary>Marks a funded campaign's pledges batch by batch, from its cursor, until done or this pass's share is spent.</summary>
    private async Task MarkToCharge(Guid campaignId, CancellationToken cancellationToken)
    {
        for (var batch = 0; batch < marking.MarkBatchesPerPass; batch++)
        {
            var marked = await transaction.Run(async ct =>
            {
                var pledges = await closing.MarkNextBatch(campaignId, marking.MarkBatchSize, ct);
                var now = clock.GetUtcNow();
                foreach (var pledge in pledges)
                {
                    await outbox.Add(new PledgeChargeRequested(
                        Guid.CreateVersion7(), now, pledge.PledgeId, campaignId, pledge.BackerId, pledge.AmountMinor, pledge.Currency, pledge.PaymentMethodRef), ct);
                }

                return Result.Success(pledges.Count);
            }, cancellationToken);
            if (!marked.IsSuccess || marked.Value < marking.MarkBatchSize)
            {
                return;
            }
        }
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
        if (!funded)
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
