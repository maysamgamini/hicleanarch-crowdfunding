using BuildingBlocks.Application;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure;

/// <summary>
/// Every 30 seconds, on every task: close and decide the campaigns whose deadline has passed.
/// The deadline only says when; the fence and the conditional updates make it correct, so a pass
/// that runs twice, or late, decides each campaign once.
/// </summary>
internal sealed partial class CloseJob(IServiceScopeFactory scopes, IConfiguration configuration, PledgesJobsConnection jobs, ILogger<CloseJob> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(configuration.GetValue("Pledges:CloseEvery", Every));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope(); // a fresh DbContext per pass
                // The decision can move many pledges in one transaction: it runs as the jobs role,
                // which the request path's two-second limit does not apply to.
                jobs.Use(scope.ServiceProvider.GetRequiredService<PledgesDbContext>());
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
