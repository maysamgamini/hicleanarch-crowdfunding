namespace Pledges.Application;

/// <summary>
/// A cached "sold out" for each limited tier, in front of the tier's row (lesson s3-a03). The row
/// stays the truth: a flag only ever answers "no" sooner, and a missing flag sends the pledge to
/// the row as before.
/// </summary>
public interface ISoldOutTiers
{
    Task<bool> IsSoldOut(Guid tierId, CancellationToken cancellationToken);

    /// <summary>Set by the pledge path the moment the row has no place left.</summary>
    Task MarkSoldOut(Guid tierId, CancellationToken cancellationToken);

    /// <summary>A place came back: the next pledge goes to the row again.</summary>
    Task Clear(Guid tierId, CancellationToken cancellationToken);
}
