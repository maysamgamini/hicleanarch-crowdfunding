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
internal sealed class TimeFencedFundingWindows(PledgesDbContext db, EfFundingWindows rowLocked) : IFundingWindows
{
    public async Task<bool> TryAccept(Guid campaignId, long deltaMinor, int backerDelta, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM pledges.funding_windows
                           WHERE campaign_id = {campaignId} AND status = 'LIVE' AND now() < deadline) AS "Value"
            """).SingleAsync(cancellationToken);

    public Task<bool> TryOpen(FundingWindow window, IReadOnlyList<TierCounter> tiers, CancellationToken cancellationToken) =>
        rowLocked.TryOpen(window, tiers, cancellationToken);

    public Task<CancelOutcome> TryCancel(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.TryCancel(campaignId, cancellationToken);

    public Task<FundingWindow?> Find(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.Find(campaignId, cancellationToken);

    public Task<bool> IsOpen(Guid campaignId, CancellationToken cancellationToken) =>
        rowLocked.IsOpen(campaignId, cancellationToken);

    // Round 2 writes no snapshot, so a dropped pledge has nothing to take out of it: its DROP entry is
    // in the ledger, which the aggregator and the checks sum, and a decided campaign shows its final sum.
    public Task TakeOutDropped(Guid campaignId, long amountMinor, CancellationToken cancellationToken) => Task.CompletedTask;
}
