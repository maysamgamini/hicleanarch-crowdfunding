namespace Pledges.Application;

/// <summary>
/// Exact sums of a campaign's ledger, for the close and the nightly check. A sum names the time range
/// it reads (from the moment the funding window opened), so the partitioned ledger reads only those months.
/// </summary>
public interface ILedgerSums
{
    Task<long> SumSinceOpened(Guid campaignId, CancellationToken cancellationToken);
}
