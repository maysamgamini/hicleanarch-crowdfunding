using Campaigns.Domain;

namespace Campaigns.Application;

/// <summary>Reads for pages, from the reader endpoint: a moment behind the writer, never used to decide.</summary>
public interface ICampaignReads
{
    Task<Campaign?> Find(CampaignId id, CancellationToken cancellationToken);
}
