using System.Diagnostics.Metrics;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>A handler of one module for one event type, as registered by that module.</summary>
public sealed record EventHandlerRegistration(Type EventType, string Handler, string Module, Func<IServiceProvider, IntegrationEvent, CancellationToken, Task> Invoke);

/// <summary>An event type some module of this program publishes.</summary>
public sealed record PublishedEvent(Type EventType);

public sealed class OutboxOptions
{
    /// <summary>Waits between tries of a failing handler; after the last, the event stays undelivered.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>How often the age of the oldest undelivered event is measured, for the alarm.</summary>
    public TimeSpan MeasureEvery { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Hands committed events to every handler in this program, right after the commit, and once more
/// for every undelivered row when the program starts. A handler that fails is retried with growing
/// waits, then left undelivered, with its error recorded; the row is marked delivered only when
/// every handler has succeeded.
/// </summary>
public sealed partial class OutboxDispatcher(
    EventBus bus,
    IEnumerable<IModuleEvents> modules,
    IEnumerable<EventHandlerRegistration> handlers,
    IEnumerable<PublishedEvent> published,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    public const string MeterName = "CrowdFunding.Outbox";

    private static readonly Meter Meter = new(MeterName);

    private readonly Dictionary<string, Type> _knownTypes = handlers.Select(h => h.EventType)
        .Concat(published.Select(p => p.EventType))
        .Distinct()
        .ToDictionary(t => t.Name);

    private double _oldestUndeliveredSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Meter.CreateObservableGauge("outbox.oldest_undelivered_age", () => _oldestUndeliveredSeconds, "s",
            "Age of the oldest event not yet delivered to every handler");
        await Recover(stoppingToken);
        _ = Measure(stoppingToken);

        await foreach (var record in bus.Reader.ReadAllAsync(stoppingToken))
        {
            _ = DeliverInBackground(record, stoppingToken);
        }
    }

    /// <summary>Nobody awaits a background delivery, so its faults are logged here; the row stays for recovery.</summary>
    private async Task DeliverInBackground(OutboxRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await Deliver(record, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogDeliveryFailed(logger, e, record.Type, record.EventId);
        }
    }

    /// <summary>Delivers one record to every handler of its type; public so a test can deliver one twice.</summary>
    public async Task Deliver(OutboxRecord record, CancellationToken cancellationToken)
    {
        if (!_knownTypes.TryGetValue(record.Type, out var type))
        {
            // Not an event this program knows, for example during a rolling deploy: leave it for a task that does.
            LogUnknownType(logger, record.Type, record.EventId);
            return;
        }

        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(record.Payload, type, EventBus.Json)!;
        var source = modules.Single(m => m.Module == record.Module);
        var allHandled = true;
        foreach (var handler in handlers.Where(h => h.EventType == type))
        {
            var target = modules.Single(m => m.Module == handler.Module);
            allHandled &= await DeliverWithRetries(record, integrationEvent, handler, target, source, cancellationToken);
        }

        if (allHandled)
        {
            try
            {
                await source.MarkDelivered(record.EventId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Every handler has its inbox row, so delivering this again later changes nothing.
                LogDeliveryFailed(logger, e, record.Type, record.EventId);
            }
        }
    }

    private async Task<bool> DeliverWithRetries(OutboxRecord record, IntegrationEvent integrationEvent, EventHandlerRegistration handler,
        IModuleEvents target, IModuleEvents source, CancellationToken cancellationToken)
    {
        var delays = options.Value.RetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await target.HandleOnce(handler.Handler, record.EventId,
                    (scope, ct) => handler.Invoke(scope, integrationEvent, ct), cancellationToken);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await TryRecordFailure(source, record, $"{handler.Handler}: {e.Message}", cancellationToken);
                if (attempt >= delays.Count)
                {
                    LogGaveUp(logger, e, handler.Handler, record.Type, record.EventId);
                    return false;
                }

                await Task.Delay(delays[attempt], cancellationToken);
            }
        }
    }

    /// <summary>
    /// Records why a handler failed. When the database is the cause, this fails too; that is logged,
    /// and the row simply stays undelivered for the next recovery.
    /// </summary>
    private async Task TryRecordFailure(IModuleEvents source, OutboxRecord record, string error, CancellationToken cancellationToken)
    {
        try
        {
            await source.RecordFailure(record.EventId, error, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogRecordFailed(logger, e, record.Type, record.EventId);
        }
    }

    private async Task Recover(CancellationToken cancellationToken)
    {
        foreach (var module in modules)
        {
            try
            {
                foreach (var record in await module.Undelivered(cancellationToken))
                {
                    bus.Publish(record);
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // The database has not been migrated yet; there is nothing to recover.
                LogNotMigrated(logger, module.Module);
            }
        }
    }

    private async Task Measure(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.MeasureEvery);
        do
        {
            try
            {
                var ages = await Task.WhenAll(modules.Select(m => m.OldestUndeliveredAge(cancellationToken)));
                _oldestUndeliveredSeconds = ages.Max(a => a?.TotalSeconds ?? 0);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogMeasureFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No handler in this program knows {Type}; event {EventId} stays undelivered")]
    private static partial void LogUnknownType(ILogger logger, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Handler} failed on {Type} {EventId} after every retry; it stays undelivered")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, string handler, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering {Type} {EventId} failed; the row stays undelivered for recovery")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record a failure for {Type} {EventId}; the row stays undelivered for recovery")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Module} outbox does not exist yet; run migrate")]
    private static partial void LogNotMigrated(ILogger logger, string module);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not measure the outbox")]
    private static partial void LogMeasureFailed(ILogger logger, Exception exception);
}
