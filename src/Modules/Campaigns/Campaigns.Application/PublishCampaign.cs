using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;
using Campaigns.Domain;

using Microsoft.Extensions.DependencyInjection;

namespace Campaigns.Application;

public sealed record PublishCampaign(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

/// <summary>
/// The domain decides whether the campaign may be published; the database makes the move only if
/// the campaign is still a draft, so two publishes at once cannot both succeed. CampaignPublished
/// is written to the outbox in the same transaction as the move.
/// </summary>
public sealed class PublishCampaignHandler(
    ICampaignRepository campaigns,
    TimeProvider clock,
    [FromKeyedServices(CampaignsModuleName.Value)] IOutbox outbox) : ICommandHandler<PublishCampaign, CampaignStatus>
{
    public async Task<Result<CampaignStatus>> Handle(PublishCampaign command, CancellationToken cancellationToken)
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

        var now = clock.GetUtcNow();
        var published = campaign.Publish(now);
        if (!published.IsSuccess)
        {
            return published;
        }

        if (!await campaigns.TryPublish(campaign.Id, now, cancellationToken))
        {
            return Result.Failure<CampaignStatus>(CampaignErrors.NotADraft);
        }

        await outbox.Add(new CampaignPublished(
            Guid.CreateVersion7(),
            now,
            campaign.Id.Value,
            campaign.CreatorId,
            campaign.Goal.AmountMinor,
            campaign.Goal.Currency,
            campaign.Deadline,
            [.. campaign.Tiers.Select(t => new PublishedTier(t.Id.Value, t.MinPledgeMinor, t.Limit))]), cancellationToken);
        return published;
    }
}
