using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// Pledges' answer to a creator's cancel, in one transaction of its own: the funding window moves
/// LIVE to CANCELLED with the close's condition, the campaign's live pledges are released (up to
/// <see cref="MarkingOptions.CancelReleaseBatchSize"/>, inside the request path's time limit), and
/// <see cref="FundingCancelled"/> and a <see cref="PledgeReleased"/> per pledge are written to the
/// outbox with it. Pledges no longer take the window's lock (lesson s3-l05), so one that read LIVE
/// can still commit just after the cancel: the close job releases those and any the first batch left,
/// once the fence's wait is behind. Asking again after an accepted cancel is accepted again, and
/// publishes nothing.
/// </summary>
public sealed class CancelFundingHandler(
    IFundingWindows windows,
    IClosing closing,
    MarkingOptions marking,
    TimeProvider clock,
    [FromKeyedServices(PledgesModuleName.Value)] IOutbox outbox,
    [FromKeyedServices(PledgesModuleName.Value)] ITransactionScope transaction) : ICommandHandler<CancelFunding, CancelFundingAccepted>
{
    public Task<Result<CancelFundingAccepted>> Handle(CancelFunding command, CancellationToken cancellationToken) =>
        transaction.Run(async ct =>
        {
            switch (await windows.TryCancel(command.CampaignId, ct))
            {
                case CancelOutcome.Cancelled:
                    await CancelledPledges.ReleaseBatch(closing, outbox, clock, command.CampaignId, marking.CancelReleaseBatchSize, ct);
                    await outbox.Add(new FundingCancelled(Guid.CreateVersion7(), clock.GetUtcNow(), command.CampaignId), ct);
                    return Result.Success(new CancelFundingAccepted(command.CampaignId));
                case CancelOutcome.AlreadyCancelled:
                    return Result.Success(new CancelFundingAccepted(command.CampaignId));
                case CancelOutcome.NotOpen:
                    return Result.Failure<CancelFundingAccepted>(PledgeErrors.FundingNotOpen);
                default:
                    return Result.Failure<CancelFundingAccepted>(PledgeErrors.CampaignClosed);
            }
        }, cancellationToken);
}

/// <summary>A cancelled campaign's release, as the cancel and the close job both run it.</summary>
internal static class CancelledPledges
{
    /// <summary>One batch of a cancelled campaign's pledges released, with a <see cref="PledgeReleased"/> each, in the caller's transaction.</summary>
    public static async Task<Result<int>> ReleaseBatch(IClosing closing, IOutbox outbox, TimeProvider clock, Guid campaignId, int batchSize, CancellationToken cancellationToken)
    {
        var pledges = await closing.ReleaseNextBatch(campaignId, batchSize, cancellationToken);
        var now = clock.GetUtcNow();
        await outbox.AddRange([.. pledges.Select(pledge => new PledgeReleased(Guid.CreateVersion7(), now, pledge.PledgeId, campaignId, pledge.BackerId))], cancellationToken);
        return Result.Success(pledges.Count);
    }
}
