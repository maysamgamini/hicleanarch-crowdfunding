using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Pledges.Application;

namespace Pledges.Infrastructure.Totals;

/// <summary>
/// Recomputes each changed campaign's display totals from the ledger, at most every two seconds per
/// campaign, and stores the absolute values. Every minute it also marks every live campaign, so a
/// signal lost in a crash costs at most a minute of staleness. It reads the read side and writes
/// only the totals store (Valkey), never the database.
/// </summary>
internal sealed partial class TotalsAggregator(
    IServiceScopeFactory scopes,
    DirtyCampaigns dirty,
    ILogger<TotalsAggregator> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DriftCheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        var nextDriftCheck = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var source = scope.ServiceProvider.GetRequiredService<ITotalsSource>();
                if (DateTimeOffset.UtcNow >= nextDriftCheck)
                {
                    foreach (var campaignId in await source.Live(stoppingToken))
                    {
                        dirty.Mark(campaignId);
                    }

                    nextDriftCheck = DateTimeOffset.UtcNow + DriftCheckEvery;
                }

                var store = scope.ServiceProvider.GetRequiredService<ITotalsStore>();
                foreach (var campaignId in dirty.TakeDue())
                {
                    if (await source.Compute(campaignId, stoppingToken) is { } totals)
                    {
                        await store.Set(campaignId, totals, stoppingToken);
                    }
                    else
                    {
                        dirty.Mark(campaignId); // not on the reader yet: next time it is due
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The totals aggregator's pass failed; the next pass tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
