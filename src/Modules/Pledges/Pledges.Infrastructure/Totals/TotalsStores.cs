using System.Collections.Concurrent;
using System.Text.Json;

using Pledges.Application;

using StackExchange.Redis;

namespace Pledges.Infrastructure.Totals;

/// <summary>Display totals in this process only: for development and tests, where there is one task.</summary>
internal sealed class InMemoryTotalsStore : ITotalsStore
{
    private readonly ConcurrentDictionary<Guid, DisplayTotals> _totals = new();

    public Task Set(Guid campaignId, DisplayTotals totals, CancellationToken cancellationToken)
    {
        _totals[campaignId] = totals;
        return Task.CompletedTask;
    }

    public Task<DisplayTotals?> Get(Guid campaignId, CancellationToken cancellationToken) =>
        Task.FromResult(_totals.GetValueOrDefault(campaignId));
}

/// <summary>
/// Display totals in Valkey, shared by every task: one key per campaign, written with a plain SET of
/// the absolute value, so a repeat or a late write never adds anything twice.
/// </summary>
internal sealed class ValkeyTotalsStore(IConnectionMultiplexer valkey) : ITotalsStore
{
    // A campaign no one has pledged to for a day is recomputed on its next read.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public static string Key(Guid campaignId) => $"pledges:totals:{campaignId:N}";

    public Task Set(Guid campaignId, DisplayTotals totals, CancellationToken cancellationToken) =>
        valkey.GetDatabase().StringSetAsync(Key(campaignId), JsonSerializer.Serialize(totals), KeepFor);

    public async Task<DisplayTotals?> Get(Guid campaignId, CancellationToken cancellationToken)
    {
        var value = await valkey.GetDatabase().StringGetAsync(Key(campaignId));
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DisplayTotals>(value.ToString());
    }
}
