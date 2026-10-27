using Microsoft.EntityFrameworkCore;

using Pledges.Application;
using Pledges.Domain;

namespace Pledges.Infrastructure.Persistence;

/// <summary>
/// Round 2's adapter for the same port (lesson s3-l05). A pledge only reads the window: no snapshot
/// update, no row lock, so pledges to one campaign no longer queue on one row. The fence is kept
/// by time instead: the request path's role is capped by transaction_timeout, and the close waits
/// past that cap before it sums. Everything else is Round 1's adapter, unchanged.
/// </summary>
internal sealed class TimeFencedFundingWindows(PledgesDbContext db, EfFundingWindows rowLocked, TimeFence fence) : IFundingWindows
{
    // The role's transaction_timeout is only the default a session starts with: PostgreSQL lets any
    // session SET it, even to 0 (context "user"). So the read that lets a pledge in also reads the
    // limit in force, in the same statement, and refuses to go on without one the fence can rely on.
    public async Task<bool> TryAccept(Guid campaignId, CancellationToken cancellationToken)
    {
        var read = await db.Database.SqlQuery<FenceRead>($"""
            SELECT EXISTS (SELECT 1 FROM pledges.funding_windows
                           WHERE campaign_id = {campaignId} AND status = 'LIVE' AND now() < deadline) AS is_open,
                   current_setting('transaction_timeout')::interval AS time_limit
            """).SingleAsync(cancellationToken);
        if (read.TimeLimit <= TimeSpan.Zero || read.TimeLimit > fence.TransactionLimit)
        {
            throw new InvalidOperationException(
                $"This transaction's transaction_timeout is {read.TimeLimit}, not at most {fence.TransactionLimit}: the close's time fence would not hold, so no pledge is accepted.");
        }

        return read.IsOpen;
    }

    public Task<bool> TryOpen(FundingWindow window, IReadOnlyList<TierCounter> tiers, CancellationToken cancellationToken) =>
        rowLocked.TryOpen(window, tiers, cancellationToken);

    public Task<CancelOutcome> TryCancel(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.TryCancel(campaignId, cancellationToken);

    public Task<FundingWindow?> Find(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.Find(campaignId, cancellationToken);

    public Task<bool> IsOpen(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.IsOpen(campaignId, cancellationToken);
}

/// <summary>Whether pledging is open, and the transaction time limit in force while it was read.</summary>
internal sealed record FenceRead(bool IsOpen, TimeSpan TimeLimit);
