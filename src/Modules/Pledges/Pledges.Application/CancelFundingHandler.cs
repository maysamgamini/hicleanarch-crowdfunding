using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

public sealed class CancelFundingHandler(IFundingWindows windows) : ICommandHandler<CancelFunding, FundingCancelled>
{
    public async Task<Result<FundingCancelled>> Handle(CancelFunding command, CancellationToken cancellationToken) =>
        await windows.TryCancel(command.CampaignId, cancellationToken) switch
        {
            CancelOutcome.Cancelled or CancelOutcome.AlreadyCancelled => Result.Success(new FundingCancelled(command.CampaignId)),
            CancelOutcome.NotOpen => Result.Failure<FundingCancelled>(PledgeErrors.FundingNotOpen),
            _ => Result.Failure<FundingCancelled>(PledgeErrors.CampaignClosed),
        };
}
