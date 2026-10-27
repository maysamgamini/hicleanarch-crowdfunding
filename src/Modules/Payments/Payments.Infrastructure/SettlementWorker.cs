using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Payments.Application;

namespace Payments.Infrastructure;

/// <summary>
/// On every task, a worker with a name of its own claims settlement jobs one at a time. It keeps
/// claiming while there is work; when there is none it waits for the next poll. When the task is
/// told to stop, the worker ends its pass and gives its job back, so another task's worker carries
/// on at once instead of waiting for the lease to run out. A task that dies without warning only
/// makes the others wait for the lease.
/// </summary>
internal sealed partial class SettlementWorker(IServiceScopeFactory scopes, IOptions<SettlementOptions> options, ILogger<SettlementWorker> logger)
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

    /// <summary>Ends the pass in progress, then gives back any job this worker still holds.</summary>
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

    public override void Dispose()
    {
        _stopping.Dispose();
        _pass.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A settlement pass failed; its lease runs out and a later pass carries on")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
