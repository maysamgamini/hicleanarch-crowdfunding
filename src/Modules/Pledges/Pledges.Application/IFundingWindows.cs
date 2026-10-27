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
}

public enum CancelOutcome
{
    Cancelled,
    AlreadyCancelled,
    Closed,
    NotOpen,
}
