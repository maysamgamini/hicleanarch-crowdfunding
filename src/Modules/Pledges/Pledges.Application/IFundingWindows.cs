using Pledges.Domain;

namespace Pledges.Application;

/// <summary>The funding window's rules, named after what they guarantee.</summary>
public interface IFundingWindows
{
    /// <summary>
    /// Opens a campaign's funding window with its tier counters, once. False: it was already open,
    /// because the same <c>CampaignPublished</c> arrived before.
    /// </summary>
    Task<bool> TryOpen(FundingWindow window, IReadOnlyList<TierCounter> tiers, CancellationToken cancellationToken);

    /// <summary>
    /// Closes funding because the creator cancels: LIVE to CANCELLED only while the deadline has not
    /// passed, by the database's clock, as the close does.
    /// </summary>
    Task<CancelOutcome> TryCancel(Guid campaignId, CancellationToken cancellationToken);

    Task<FundingWindow?> Find(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The fence's pledge side: adds to the snapshot total only while the window is LIVE and before
    /// the deadline by the database's clock, and holds the window's row lock until this transaction
    /// ends, so the close waits for it. False: pledging has closed.
    /// </summary>
    Task<bool> TryAccept(Guid campaignId, long deltaMinor, int backerDelta, CancellationToken cancellationToken);
}

public enum CancelOutcome
{
    Cancelled,
    AlreadyCancelled,
    Closed,
    NotOpen,
}
