namespace Pledges.Application;

/// <summary>
/// In a campaign's last 24 hours, a cancel or decrease may not take a funded campaign below its
/// goal (lesson s3-l06). Only paths that lower the sum ask; new pledges and increases cannot break it.
/// </summary>
public interface ILastDayRule
{
    /// <summary>
    /// True when lowering the campaign's sum by <paramref name="decreaseMinor"/> keeps the rule. On
    /// the last day, cancels and decreases of one campaign take turns until this transaction ends,
    /// so two of them cannot both see the same sum.
    /// </summary>
    Task<bool> Allows(Guid campaignId, long decreaseMinor, CancellationToken cancellationToken);
}
