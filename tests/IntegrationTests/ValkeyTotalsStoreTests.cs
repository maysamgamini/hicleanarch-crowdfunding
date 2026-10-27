using Pledges.Application;
using Pledges.Infrastructure.Totals;

using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>The aggregator's store in Valkey (lesson s3-l05): absolute values, so a repeat or a late write adds nothing.</summary>
public sealed class ValkeyTotalsStoreTests(Valkey valkey) : IClassFixture<Valkey>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stored_total_is_read_back_and_a_later_write_replaces_it()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(valkey.Configuration);
        var store = new ValkeyTotalsStore(connection);
        var campaignId = Guid.NewGuid();
        var tier = Guid.NewGuid();
        Assert.Null(await store.Get(campaignId, Ct));

        await store.Set(campaignId, new DisplayTotals(6_000, 1, new Dictionary<Guid, int> { [tier] = 9 }, DateTimeOffset.UtcNow), Ct);
        await store.Set(campaignId, new DisplayTotals(6_000, 1, new Dictionary<Guid, int> { [tier] = 9 }, DateTimeOffset.UtcNow), Ct);
        await store.Set(campaignId, new DisplayTotals(15_900, 2, new Dictionary<Guid, int> { [tier] = 8 }, DateTimeOffset.UtcNow), Ct);

        var read = await store.Get(campaignId, Ct);
        Assert.Equal(15_900, read!.PledgedMinor);
        Assert.Equal(2, read.Backers);
        Assert.Equal(8, read.TierRemaining[tier]);
        Assert.True(await connection.GetDatabase().KeyTimeToLiveAsync(ValkeyTotalsStore.Key(campaignId)) > TimeSpan.FromHours(23));
    }
}
