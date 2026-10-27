using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Pledges.Application;

namespace Pledges.Infrastructure;

/// <summary>
/// Every 10 seconds, on every task: expire the holds that ran out. Running it on several tasks at
/// once is safe, because each expiry is a conditional update; a second task changes 0 rows.
/// </summary>
internal sealed partial class HoldSweeper(IServiceScopeFactory scopes, ILogger<HoldSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var expired = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<ExpireHolds, int>(new ExpireHolds(), stoppingToken);
                if (expired.Value > 0)
                {
                    LogExpired(logger, expired.Value);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} holds")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hold sweeper's pass failed; the next pass tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
