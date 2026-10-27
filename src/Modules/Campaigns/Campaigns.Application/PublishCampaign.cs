using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Domain;

namespace Campaigns.Application;

public sealed record PublishCampaign(CampaignId Id, Guid CallerId) : ICommand<CampaignStatus>;

/// <summary>
/// The domain decides whether the campaign may be published; the database makes the move only if
/// the campaign is still a draft, so two publishes at once cannot both succeed.
/// </summary>
public sealed class PublishCampaignHandler(ICampaignRepository campaigns, TimeProvider clock)
    : ICommandHandler<PublishCampaign, CampaignStatus>
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

        return await campaigns.TryPublish(campaign.Id, now, cancellationToken)
            ? published
            : Result.Failure<CampaignStatus>(CampaignErrors.NotADraft);
    }
}
