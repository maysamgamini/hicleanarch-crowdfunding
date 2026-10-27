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

    /// <summary>
    /// Reads a pledge and locks its row until the transaction ends, so two changes of one pledge
    /// run one after the other, each from the pledge as the last one left it.
    /// </summary>
    Task<Pledge?> FindForChange(Guid pledgeId, CancellationToken cancellationToken);

    /// <summary>Saves a changed or cancelled pledge, only if it is still ACTIVE. False: it no longer was.</summary>
    Task<bool> TrySaveFromActive(Pledge pledge, CancellationToken cancellationToken);
}

/// <summary>The promise ledger. It can only be appended to; the module's role cannot update or delete it.</summary>
public interface IPledgeLedger
{
    Task Append(LedgerEntry entry, CancellationToken cancellationToken);
}

/// <summary>A campaign's tiers, as Pledges counts them. Only limited tiers are counted.</summary>
public interface ITierSlots
{
    Task<TierCounter?> Find(Guid tierId, CancellationToken cancellationToken);

    /// <summary>Takes a place for an active pledge, only if one is left. False: sold out.</summary>
    Task<bool> TryClaim(Guid tierId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a pledge's place from one tier to another, either of which may be none or unlimited.
    /// Both rows are locked in tier id order first, so two backers swapping in opposite directions
    /// cannot deadlock. False: the new tier is sold out; the caller rolls back, which also undoes the release.
    /// </summary>
    Task<bool> TryMove(Guid? fromTierId, Guid? toTierId, CancellationToken cancellationToken);
}
