using Microsoft.Extensions.Logging;

using Pledges.Application;

using StackExchange.Redis;

namespace Pledges.Infrastructure.HotTier;

/// <summary>
/// The sold-out flags in Valkey, one key per tier. A flag lives for <see cref="Lifetime"/> and is
/// set again by the next pledge that finds the row full, so a flag set by a transaction that then
/// rolled back, or not cleared when a hold expired, lasts no longer than that. Valkey unreachable
/// means no flag: the pledge goes to the row.
/// </summary>
internal sealed partial class ValkeySoldOutTiers(IConnectionMultiplexer valkey, ILogger<ValkeySoldOutTiers> logger) : ISoldOutTiers
{
    /// <summary>The aggregator's period in s3-a03: the longest a flag can be wrong in the safe direction.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    private static RedisKey Key(Guid tierId) => $"pledges:tier-sold-out:{tierId:N}";

    public async Task<bool> IsSoldOut(Guid tierId, CancellationToken cancellationToken)
    {
        try
        {
            return await valkey.GetDatabase().KeyExistsAsync(Key(tierId));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnreachable(logger, e);
            return false;
        }
    }

    public Task MarkSoldOut(Guid tierId, CancellationToken cancellationToken) =>
        Try(db => db.StringSetAsync(Key(tierId), "1", Lifetime));

    public Task Clear(Guid tierId, CancellationToken cancellationToken) =>
        Try(db => db.KeyDeleteAsync(Key(tierId)));

    private async Task Try(Func<IDatabase, Task> write)
    {
        try
        {
            await write(valkey.GetDatabase());
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            LogUnreachable(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey could not be reached for a tier's sold-out flag; the row decides")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);
}

/// <summary>No Valkey, no flags: every pledge goes to the row, as in Stage 2.</summary>
internal sealed class NoSoldOutTiers : ISoldOutTiers
{
    public Task<bool> IsSoldOut(Guid tierId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task MarkSoldOut(Guid tierId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task Clear(Guid tierId, CancellationToken cancellationToken) => Task.CompletedTask;
}
