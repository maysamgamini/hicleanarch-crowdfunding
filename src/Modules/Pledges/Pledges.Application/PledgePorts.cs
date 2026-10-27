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

    /// <summary>
    /// PENDING_SETUP to ACTIVE with the saved card, only while its hold has not run out by the
    /// database's clock. False: it was not waiting, or it was too late; EXPIRED is final.
    /// </summary>
    Task<bool> TryActivate(Guid pledgeId, string paymentMethodRef, CancellationToken cancellationToken);

    /// <summary>ACTIVE back to EXPIRED, in the same transaction that activated it, when the campaign had closed meanwhile.</summary>
    Task<bool> TryExpireActivated(Guid pledgeId, CancellationToken cancellationToken);
}

/// <summary>Pledges waiting for a card, whose places are held.</summary>
public interface IHolds
{
    /// <summary>
    /// Expires waiting pledges whose hold has run out, each in its own short transaction, and gives
    /// their tier places back. Returns how many it expired.
    /// </summary>
    Task<int> ExpireDue(int batchSize, CancellationToken cancellationToken);
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

    /// <summary>Keeps a place for a pledge waiting for its card, only if one is left. False: sold out.</summary>
    Task<bool> TryHold(Guid tierId, CancellationToken cancellationToken);

    /// <summary>The card was saved in time: the held place becomes a claimed one.</summary>
    Task HoldToClaim(Guid tierId, CancellationToken cancellationToken);

    /// <summary>A hold ends without a card: its place is free again.</summary>
    Task ReleaseHold(Guid tierId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a pledge's place from one tier to another, either of which may be none or unlimited.
    /// Both rows are locked in tier id order first, so two backers swapping in opposite directions
    /// cannot deadlock. False: the new tier is sold out; the caller rolls back, which also undoes the release.
    /// </summary>
    Task<bool> TryMove(Guid? fromTierId, Guid? toTierId, CancellationToken cancellationToken);
}
