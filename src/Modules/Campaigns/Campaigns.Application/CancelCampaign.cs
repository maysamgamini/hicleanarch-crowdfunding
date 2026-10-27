using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;
using Campaigns.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Campaigns.Application;

/// <summary>
/// Asks Pledges first, outside Campaigns' own transaction: a live campaign can be cancelled only if
/// Pledges, which owns the deadline, accepts <see cref="CancelFunding"/>. A cancel can lose the race
/// with the close, so it is a command with an answer, never an event. A draft is Campaigns' alone.
/// </summary>
public sealed record AskToCancelFunding(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

public sealed class AskToCancelFundingHandler(ICampaignRepository campaigns, IDispatcher dispatcher)
    : ICommandHandler<AskToCancelFunding, CampaignStatus>
{
    public async Task<Result<CampaignStatus>> Handle(AskToCancelFunding command, CancellationToken cancellationToken)
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

        if (campaign.Status != CampaignStatus.Live)
        {
            return Result.Success(campaign.Status);
        }

        var funding = await dispatcher.Send<CancelFunding, CancelFundingAccepted>(new CancelFunding(campaign.Id.Value), cancellationToken);
        return funding.IsSuccess ? Result.Success(campaign.Status) : Result.Failure<CampaignStatus>(funding.Errors);
    }
}

public sealed record CancelCampaign(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

/// <summary>
/// Records the cancel in the catalog, in Campaigns' own transaction, once Pledges has accepted it
/// (or for a draft), and tells whoever listens.
/// </summary>
public sealed class CancelCampaignHandler(
    ICampaignRepository campaigns,
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

        if (campaign.Status == CampaignStatus.Cancelled)
        {
            return Result.Success(CampaignStatus.Cancelled); // the reconciliation got here first
        }

        var from = campaign.Status;
        var cancelled = campaign.Cancel();
        if (!cancelled.IsSuccess)
        {
            return cancelled;
        }

        if (!await campaigns.TryCancel(campaign.Id, from, cancellationToken))
        {
            // Pledges' FundingCancelled may have reached the catalog first; then it is done, and told.
            return (await campaigns.Find(campaign.Id, cancellationToken))?.Status == CampaignStatus.Cancelled
                ? Result.Success(CampaignStatus.Cancelled)
                : Result.Failure<CampaignStatus>(CampaignErrors.NotCancellable);
        }

        await outbox.Add(new CampaignCancelled(Guid.CreateVersion7(), clock.GetUtcNow(), campaign.Id.Value, campaign.CreatorId), cancellationToken);
        return cancelled;
    }
}

/// <summary>
/// Pledges accepted a cancel. If the request that asked for it failed before Campaigns recorded it,
/// this records it now: an accepted cancel always reaches the catalog. The move is conditional, so
/// when the request already recorded it, nothing happens and nothing is published twice.
/// </summary>
public sealed class RecordFundingCancelled(
    ICampaignRepository campaigns,
    TimeProvider clock,
    [FromKeyedServices(CampaignsModuleName.Value)] IOutbox outbox) : IIntegrationEventHandler<FundingCancelled>
{
    public async Task Handle(FundingCancelled cancelled, CancellationToken cancellationToken)
    {
        var id = new CampaignId(cancelled.CampaignId);
        var campaign = await campaigns.Find(id, cancellationToken);
        if (campaign is not null && await campaigns.TryCancel(id, CampaignStatus.Live, cancellationToken))
        {
            await outbox.Add(new CampaignCancelled(Guid.CreateVersion7(), clock.GetUtcNow(), campaign.Id.Value, campaign.CreatorId), cancellationToken);
        }
    }
}
