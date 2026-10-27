using Pledges.Domain;

namespace Pledges.Application;

/// <summary>Pledges, by the rule that guards them.</summary>
public interface IPledges
{
    /// <summary>
    /// Adds the pledge unless the backer already has a live one on this campaign. False: already
    /// backing. The database's partial unique index decides, whichever code path sends it.
    /// </summary>
    Task<bool> TryAdd(Pledge pledge, CancellationToken cancellationToken);

    Task<Pledge?> Find(Guid pledgeId, CancellationToken cancellationToken);
}

/// <summary>The promise ledger. It can only be appended to; the module's role cannot update or delete it.</summary>
public interface IPledgeLedger
{
    Task Append(LedgerEntry entry, CancellationToken cancellationToken);
}

/// <summary>A campaign's tiers, as Pledges counts them.</summary>
public interface ITierSlots
{
    Task<TierCounter?> Find(Guid tierId, CancellationToken cancellationToken);
}
