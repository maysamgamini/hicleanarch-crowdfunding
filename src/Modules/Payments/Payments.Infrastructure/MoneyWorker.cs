using BuildingBlocks.Application;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Payments.Application;
using Payments.Infrastructure.Cutover;

namespace Payments.Infrastructure;

/// <summary>
/// On every task, the money pass runs on a timer: reconcile, then pay out. Each step is a
/// conditional move and every PSP call has its own key, so two tasks never do a step twice. No pass
/// runs while a data move pauses Payments.
/// </summary>
internal sealed partial class MoneyWorker(IServiceScopeFactory scopes, MoneyOptions options, IPaymentsPause pause, ILogger<MoneyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (await pause.IsSet(stoppingToken))
                {
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                var pass = (await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                    .Send<RunMoneyPass, MoneyPass>(new RunMoneyPass(), stoppingToken)).Value;
                if (pass.Differing > 0)
                {
                    LogDifferences(logger, pass.Differing);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Campaigns} campaigns differ from the PSP's report; their payouts wait until the differences are explained")]
    private static partial void LogDifferences(ILogger logger, int campaigns);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A money pass failed; the next one carries on")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
