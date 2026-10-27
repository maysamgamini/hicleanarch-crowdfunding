namespace Pledges.Infrastructure.Sharding;

/// <summary>
/// The shard key seam: data access asks for the database of a campaign, never for "the database".
/// Today there is one shard. On the day Pledges is sharded by campaign, only this lookup changes.
/// </summary>
internal interface ICampaignShards
{
    string ConnectionStringFor(Guid campaignId);
}

/// <summary>Every campaign lives on the one cluster.</summary>
internal sealed class SingleShard(string connectionString) : ICampaignShards
{
    public string ConnectionStringFor(Guid campaignId) => connectionString;
}
