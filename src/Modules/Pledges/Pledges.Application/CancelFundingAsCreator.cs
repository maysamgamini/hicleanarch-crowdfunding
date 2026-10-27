using BuildingBlocks.Application;
using BuildingBlocks.Domain;

using Campaigns.Contracts;

using Pledges.Contracts;
using Pledges.Domain;

namespace Pledges.Application;

/// <summary>
/// A creator's cancel of a LIVE campaign, received by Pledges itself (lesson s5-l05): the service
/// that owns the deadline answers. The caller must be the creator Pledges stored for the window; a
/// window whose creator is not known yet is refused, never let through.
/// </summary>
public sealed record CancelFundingAsCreator(Guid CampaignId, Guid CallerId) : ICommand<CancelFundingAccepted>;

public sealed class CancelFundingAsCreatorHandler(IFundingWindowCreators creators, IDispatcher dispatcher)
    : ICommandHandler<CancelFundingAsCreator, CancelFundingAccepted>
{
    public async Task<Result<CancelFundingAccepted>> Handle(CancelFundingAsCreator command, CancellationToken cancellationToken)
    {
        var creator = await creators.CreatorOf(command.CampaignId, cancellationToken);
        if (creator is not { } known)
        {
            return Result.Failure<CancelFundingAccepted>(PledgeErrors.CampaignNotFound);
        }

        if (known.CreatorId is not { } creatorId)
        {
            return Result.Failure<CancelFundingAccepted>(PledgeErrors.CreatorUnknown);
        }

        // The creator never changes, so reading it before the cancel's own transaction is safe.
        return creatorId != command.CallerId
            ? Result.Failure<CancelFundingAccepted>(PledgeErrors.NotYourCampaign)
            : await dispatcher.Send<CancelFunding, CancelFundingAccepted>(new CancelFunding(command.CampaignId), cancellationToken);
    }
}

/// <summary>The backfill's other half: a window opened before Pledges stored creators gets its creator.</summary>
public sealed class RecordCreator(IFundingWindowCreators creators) : IIntegrationEventHandler<CampaignCreatorRecorded>
{
    public Task Handle(CampaignCreatorRecorded recorded, CancellationToken cancellationToken) =>
        creators.TryRecord(recorded.CampaignId, recorded.CreatorId, cancellationToken);
}

/// <summary>The creator Pledges keeps on each funding window.</summary>
public interface IFundingWindowCreators
{
    /// <summary>Null: no window for this campaign. A window with a null creator was opened before Pledges stored it.</summary>
    Task<WindowCreator?> CreatorOf(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the creator only where it is still null. A different creator already stored is never
    /// overwritten: the adapter logs it and counts it.
    /// </summary>
    Task<CreatorRecorded> TryRecord(Guid campaignId, Guid creatorId, CancellationToken cancellationToken);

    /// <summary>LIVE windows with no creator: the funding cancel opens only when there are none.</summary>
    Task<long> OpenWithoutCreator(CancellationToken cancellationToken);
}

public sealed record WindowCreator(Guid? CreatorId);

public enum CreatorRecorded
{
    Recorded,
    AlreadyKnown,
    Different,
    NoWindow,
}
