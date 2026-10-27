using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Hosting;

/// <summary>
/// Something a task should finish or give back before it stops: a job's claims, a batch in progress.
/// Nothing here is needed for correctness; a task that dies without warning loses no money. It only
/// spares others the wait for a lease to run out.
/// </summary>
public interface IStopParticipant
{
    string Name { get; }

    Task Stop(CancellationToken cancellationToken);
}

/// <summary>
/// Runs every participant when the host starts stopping, before the hosted services stop. ECS sends
/// SIGTERM after the load balancer has drained the task; ASP.NET Core then stops taking new
/// requests and waits for the ones in progress, within <see cref="HostOptions.ShutdownTimeout"/>.
/// </summary>
public sealed partial class GracefulStop(IEnumerable<IStopParticipant> participants, ILogger<GracefulStop> logger)
    : IHostedLifecycleService
{
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        foreach (var participant in participants)
        {
            try
            {
                await participant.Stop(cancellationToken);
                LogStopped(logger, participant.Name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One participant's failure must not keep the others from giving back their work.
                LogFailed(logger, e, participant.Name);
            }
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped {Participant} before shutdown")]
    private static partial void LogStopped(ILogger logger, string participant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Participant} failed to stop cleanly; its work is left to expire")]
    private static partial void LogFailed(ILogger logger, Exception exception, string participant);
}

public static class GracefulStopRegistration
{
    /// <summary>
    /// The whole stop must fit inside ECS's stop timeout (30 seconds by default), after which the task
    /// is killed; 25 seconds leaves a margin.
    /// </summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(25);

    public static IServiceCollection AddGracefulStop(this IServiceCollection services)
    {
        services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
        services.AddHostedService<GracefulStop>();
        return services;
    }
}
