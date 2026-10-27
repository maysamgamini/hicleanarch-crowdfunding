namespace Pledges.Contracts;

/// <summary>
/// What a campaign has raised so far, for pages. It reads the snapshot, which is exact for display
/// and never used to decide anything. In process today; after a split, an HTTP call behind the
/// same interface.
/// </summary>
public interface ICampaignTotals
{
    /// <summary>Null when the campaign has no funding window yet (a draft, or a publish still on its way).</summary>
    Task<CampaignTotals?> Get(Guid campaignId, CancellationToken cancellationToken);
}

public sealed record CampaignTotals(long PledgedMinor, int Backers);
