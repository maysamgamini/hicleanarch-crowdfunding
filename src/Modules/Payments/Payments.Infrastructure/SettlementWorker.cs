using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Payments.Application;

namespace Payments.Infrastructure;

/// <summary>
/// On every task, a worker with a name of its own claims settlement jobs one at a time. It keeps
/// claiming while there is work; when there is none it waits for the next poll. A stopped task
/// leaves its lease to run out, and another task's worker carries on.
/// </summary>
internal sealed partial class SettlementWorker(IServiceScopeFactory scopes, IOptions<SettlementOptions> options, ILogger<SettlementWorker> logger) : BackgroundService
{
    private readonly string _name = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                bool worked;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    worked = (await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                        .Send<RunSettlement, bool>(new RunSettlement(_name), stoppingToken)).Value;
                }
                while (worked && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A settlement pass failed; its lease runs out and a later pass carries on")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
