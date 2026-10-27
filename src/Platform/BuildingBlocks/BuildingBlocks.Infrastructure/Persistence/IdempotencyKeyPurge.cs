using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>One module's purge, run once; the operations and tests call it by this name.</summary>
public interface IIdempotencyKeyPurge
{
    Task<int> PurgeOnce(CancellationToken cancellationToken);
}

/// <summary>
/// Points the purge's context at another connection of the same module, for a module whose request
/// role has a short transaction limit (Pledges' jobs role). Registered by that module; optional.
/// </summary>
public sealed record PurgeConnection<TContext>(Action<TContext> Use)
    where TContext : DbContext;

/// <summary>
/// Deletes a module's idempotency keys once they are older than any client keeps retrying: 7 days.
/// Every task runs it every hour; two tasks deleting the same old rows do no harm. It deletes in
/// batches, each its own short statement, so a day of keys never makes one long transaction.
/// </summary>
public sealed partial class IdempotencyKeyPurge<TContext>(IServiceScopeFactory scopes, ILogger<IdempotencyKeyPurge<TContext>> logger)
    : BackgroundService, IIdempotencyKeyPurge
    where TContext : DbContext
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    public const int BatchSize = 5_000;

    public async Task<int> PurgeOnce(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        scope.ServiceProvider.GetService<PurgeConnection<TContext>>()?.Use(db);
        var cutoff = DateTimeOffset.UtcNow - KeepFor;
        var purged = 0;
        int deleted;
        do
        {
            deleted = await db.Set<IdempotencyKey>().Where(k => k.CreatedAt < cutoff)
                .OrderBy(k => k.CreatedAt).Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            purged += deleted;
        }
        while (deleted == BatchSize);

        return purged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = await PurgeOnce(stoppingToken);
                LogPurged(logger, typeof(TContext).Name, purged);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, typeof(TContext).Name, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} old idempotency keys of {Context}")]
    private static partial void LogPurged(ILogger logger, string context, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purging old idempotency keys of {Context} failed; the next hour tries again")]
    private static partial void LogFailed(ILogger logger, string context, Exception exception);
}
