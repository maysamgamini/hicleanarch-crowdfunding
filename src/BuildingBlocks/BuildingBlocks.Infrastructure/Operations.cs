using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure;

/// <summary>
/// The numbers an operator watches: one gauge per Round 1 failure mode. Each module measures its own
/// with a <see cref="ModuleMonitor"/>; the gauges read the last measurement.
/// </summary>
public static class Operations
{
    public const string MeterName = "CrowdFunding.Operations";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>A gauge whose value a module sets after each measurement.</summary>
    public sealed class Measured(string name, string unit, string description)
    {
        private double _value;

        public double Value => Volatile.Read(ref _value);

        public void Set(double value) => Volatile.Write(ref _value, value);

        public Measured Register()
        {
            Meter.CreateObservableGauge(name, () => Value, unit, description);
            return this;
        }
    }
}

/// <summary>
/// Measures a module's own numbers every minute, in a fresh scope, from its own schema. A failed
/// measurement is logged and the gauges keep their last values until the next one.
/// </summary>
public abstract partial class ModuleMonitor(IServiceScopeFactory scopes, ILogger logger) : BackgroundService
{
    protected virtual TimeSpan Every => TimeSpan.FromMinutes(1);

    protected abstract Task Measure(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await Measure(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e, GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Monitor} could not measure; the gauges keep their last values")]
    private static partial void LogFailed(ILogger logger, Exception exception, string monitor);
}
