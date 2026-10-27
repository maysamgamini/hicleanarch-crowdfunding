using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;
using Campaigns.Domain;

using Microsoft.Extensions.DependencyInjection;

using Pledges.Contracts;

namespace Campaigns.Application;

public sealed record CancelCampaign(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

/// <summary>
/// Cancels a draft, in Campaigns' own transaction, and tells whoever listens. A live campaign's cancel is
/// Pledges' command (lesson s5-l05): since s5-l08 this path refuses it with <c>funding_cancel_moved</c>,
/// so Campaigns sends Pledges no command; the catalog follows Pledges' <see cref="FundingCancelled"/>.
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

        if (campaign.Status == CampaignStatus.Live)
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.FundingCancelMoved);
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
/// Pledges accepted a creator's cancel of a live campaign: the catalog records it. The move is
/// conditional, so a repeat of the event changes nothing and nothing is published twice.
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
