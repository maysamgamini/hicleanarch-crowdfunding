using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;
using Campaigns.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Campaigns.Application;

public sealed record CancelCampaign(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

/// <summary>
/// A draft is Campaigns' alone to cancel. A live campaign is cancelled only if Pledges, which owns
/// the deadline, accepts <see cref="CancelFunding"/>: a cancel can lose the race with the close,
/// so it is a command with an answer, never an event. Campaigns records the cancel after that, and
/// then tells whoever listens.
/// </summary>
public sealed class CancelCampaignHandler(
    ICampaignRepository campaigns,
    IDispatcher dispatcher,
    TimeProvider clock,
    [FromKeyedServices(CampaignsModuleName.Value)] IOutbox outbox) : ICommandHandler<CancelCampaign, CampaignStatus>
{
    public async Task<Result<CampaignStatus>> Handle(CancelCampaign command, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.Find(command.Id, cancellationToken);
        if (campaign is null)
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.NotFound);
        }

        if (campaign.CreatorId != command.CallerId)
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.NotYours);
        }

        var from = campaign.Status;
        var cancelled = campaign.Cancel();
        if (!cancelled.IsSuccess)
        {
            return cancelled;
        }

        if (from == CampaignStatus.Live)
        {
            var funding = await dispatcher.Send<CancelFunding, FundingCancelled>(new CancelFunding(campaign.Id.Value), cancellationToken);
            if (!funding.IsSuccess)
            {
                return Result.Failure<CampaignStatus>(funding.Errors);
            }
        }

        if (!await campaigns.TryCancel(campaign.Id, from, cancellationToken))
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.NotCancellable);
        }

        await outbox.Add(new CampaignCancelled(Guid.CreateVersion7(), clock.GetUtcNow(), campaign.Id.Value, campaign.CreatorId), cancellationToken);
        return cancelled;
    }
}
