using Npgsql;

using Pledges.Application;

namespace Pledges.Infrastructure.Sharding;

/// <summary>
/// The exact sum of one campaign's ledger, read from that campaign's shard. It bounds created_at by
/// the funding window's opening, so PostgreSQL reads only the months since then.
/// </summary>
internal sealed class ShardedLedgerSums(ICampaignShards shards) : ILedgerSums
{
    public async Task<long> SumSinceOpened(Guid campaignId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(shards.ConnectionStringFor(campaignId));
        await connection.OpenAsync(cancellationToken);

        await using var opened = new NpgsqlCommand(
            "SELECT opened_at FROM pledges.funding_windows WHERE campaign_id = @campaign", connection);
        opened.Parameters.AddWithValue("campaign", campaignId);
        if (await opened.ExecuteScalarAsync(cancellationToken) is not DateTime since)
        {
            return 0;
        }

        await using var sum = new NpgsqlCommand("""
            SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger
            WHERE campaign_id = @campaign AND created_at >= @since
            """, connection);
        sum.Parameters.AddWithValue("campaign", campaignId);
        sum.Parameters.AddWithValue("since", since);
        return (long)(await sum.ExecuteScalarAsync(cancellationToken))!;
    }
}
