using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// Pledges' answer to a creator's cancel, in one transaction of its own: the funding window moves
/// LIVE to CANCELLED with the close's condition, and <see cref="FundingCancelled"/> is written to
/// the outbox with it. Asking again after an accepted cancel is accepted again, and publishes nothing.
/// </summary>
public sealed class CancelFundingHandler(
    IFundingWindows windows,
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
