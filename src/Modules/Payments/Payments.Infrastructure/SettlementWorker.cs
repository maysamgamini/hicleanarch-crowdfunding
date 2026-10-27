using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Payments.Application;
using Payments.Infrastructure.Cutover;

namespace Payments.Infrastructure;

/// <summary>
/// On every task, a worker with a name of its own takes turns at settlement jobs, one pass at a
/// time, each pass a batch of charges with calls in flight; workers on other tasks share the same
/// campaign by claiming other charges. It keeps going while there is work; when there is none it
/// waits for the next poll. When the task is told to stop, the worker ends its pass and gives its
/// charges back, so another task's worker carries on at once instead of waiting for the leases to
/// run out. A task that dies without warning only makes the others wait for the leases. While a
/// data move pauses Payments, it claims nothing and gives back what it holds.
/// </summary>
internal sealed partial class SettlementWorker(IServiceScopeFactory scopes, IOptions<SettlementOptions> options, IPaymentsPause pause, ILogger<SettlementWorker> logger)
    : BackgroundService, IStopParticipant
{
    private readonly string _name = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _pass = new(1, 1);

    public string Name => "settlement worker";

    /// <summary>The name its leases carry.</summary>
    internal string WorkerName => _name;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stopping.Token);
        using var timer = new PeriodicTimer(options.Value.PollEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await _pass.WaitAsync(stop.Token);
                try
                {
                    bool worked;
                    do
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        if (await pause.IsSet(stop.Token))
                        {
                            await scope.ServiceProvider.GetRequiredService<ISettlements>().ReleaseAll(_name, stop.Token);
                            break;
                        }

                        worked = (await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                            .Send<RunSettlement, bool>(new RunSettlement(_name), stop.Token)).Value;
                    }
                    while (worked && !stop.IsCancellationRequested);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(logger, e);
                }
                finally
                {
                    _pass.Release();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>Ends the pass in progress, then gives back any charge this worker still holds.</summary>
    public async Task Stop(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _pass.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettlements>().ReleaseAll(_name, cancellationToken);
        }
        finally
        {
            _pass.Release();
        }
    }

    // _pass is not disposed: a host torn down while Stop still runs would make its Release throw, and a
    // SemaphoreSlim whose wait handle is never asked for holds nothing that needs disposing.
    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A settlement pass failed; its lease runs out and a later pass carries on")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
