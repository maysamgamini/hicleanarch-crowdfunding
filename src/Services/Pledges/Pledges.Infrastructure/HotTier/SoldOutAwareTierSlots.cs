using Pledges.Application;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure.HotTier;

/// <summary>
/// The tier counters of Stage 2, with the sold-out flag kept beside them: set when a take leaves no
/// place or finds none, cleared when a place is given back. The rows still decide every place.
/// </summary>
internal sealed class SoldOutAwareTierSlots(EfPledges rows, ISoldOutTiers soldOut) : ITierSlots
{
    public Task<TierCounter?> Find(Guid tierId, CancellationToken cancellationToken) => ((ITierSlots)rows).Find(tierId, cancellationToken);

    public async Task<bool> TryClaim(Guid tierId, CancellationToken cancellationToken) =>
        await Flagged(tierId, await rows.TryClaim(tierId, cancellationToken), cancellationToken);

    public async Task<bool> TryHold(Guid tierId, CancellationToken cancellationToken) =>
        await Flagged(tierId, await rows.TryHold(tierId, cancellationToken), cancellationToken);

    public Task HoldToClaim(Guid tierId, CancellationToken cancellationToken) => rows.HoldToClaim(tierId, cancellationToken);

    public async Task ReleaseHold(Guid tierId, CancellationToken cancellationToken)
    {
        await rows.ReleaseHold(tierId, cancellationToken);
        await soldOut.Clear(tierId, cancellationToken);
    }

    public async Task<bool> TryMove(Guid? fromTierId, Guid? toTierId, CancellationToken cancellationToken)
    {
        var moved = await rows.TryMove(fromTierId, toTierId, cancellationToken);
        if (moved && fromTierId is { } from && from != toTierId)
        {
            await soldOut.Clear(from, cancellationToken);
        }

        if (toTierId is { } to && to != fromTierId)
        {
            await Flagged(to, moved, cancellationToken);
        }

        return moved;
    }

    private async Task<bool> Flagged(Guid tierId, bool taken, CancellationToken cancellationToken)
    {
        if (!taken || await rows.PlacesLeft(tierId, cancellationToken) == 0)
        {
            await soldOut.MarkSoldOut(tierId, cancellationToken);
        }

        return taken;
    }
}
