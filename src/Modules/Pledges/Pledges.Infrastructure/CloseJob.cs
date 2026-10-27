using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Pledges.Application;

namespace Pledges.Infrastructure;

/// <summary>
/// Every 30 seconds, on every task: close and decide the campaigns whose deadline has passed.
/// The deadline only says when; the fence and the conditional updates make it correct, so a pass
/// that runs twice, or late, decides each campaign once.
/// </summary>
internal sealed partial class CloseJob(IServiceScopeFactory scopes, ILogger<CloseJob> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope(); // a fresh DbContext per pass
                var decided = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(), stoppingToken);
                if (decided.Value > 0)
                {
                    LogDecided(logger, decided.Value);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Decided {Count} campaigns")]
    private static partial void LogDecided(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The close job's pass failed; the next pass tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
