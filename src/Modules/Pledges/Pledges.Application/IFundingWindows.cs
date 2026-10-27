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
    /// LIVE and before the deadline by the database's clock, without taking the window's lock: for a
    /// pledge that waits for its card and does not count yet. Its activation passes the fence.
    /// </summary>
    Task<bool> IsOpen(Guid campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The fence's pledge side: accepts only while the window is LIVE and before the deadline by the
    /// database's clock, and makes sure the close does not sum the ledger before this transaction
    /// has either committed or ended. False: pledging has closed. Round 1 added to a snapshot under
    /// the window's row lock, which the close waited for; Round 2 only reads the window, and the
    /// close waits out the transactions' time limit instead (lesson s3-l05).
    /// </summary>
    Task<bool> TryAccept(Guid campaignId, CancellationToken cancellationToken);
}

public enum CancelOutcome
{
    Cancelled,
    AlreadyCancelled,
    Closed,
    NotOpen,
}
