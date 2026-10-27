using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>A handler of one module for one event type, as registered by that module.</summary>
public sealed record EventHandlerRegistration(Type EventType, string Handler, string Module, Func<IServiceProvider, IntegrationEvent, CancellationToken, Task> Invoke);

/// <summary>An event type a module of this program publishes.</summary>
public sealed record PublishedEvent(Type EventType, string Module);

public sealed class OutboxOptions
{
    /// <summary>
    /// Waits between tries of a failing handler, each drawn at random between half and all of it, so
    /// the tasks that failed together on the same outage do not retry together. After the last try the
    /// event waits for a sweep.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// How often the outboxes are swept: the age of the oldest undelivered event is measured, for the
    /// alarm, and every event undelivered for longer than <see cref="RedeliverAfter"/> is delivered again.
    /// </summary>
    public TimeSpan SweepEvery { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// An event still undelivered this long after it happened is no longer being tried by the task that
    /// committed it: longer than every retry together.
    /// </summary>
    public TimeSpan RedeliverAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The most events one task delivers at once. Each delivery holds a connection of the handler's
    /// module and then one of the event's module, from the same pools as the requests (10 a role), so
    /// a batch of thousands of events never takes them all.
    /// </summary>
    public int MaxConcurrentDeliveries { get; set; } = 4;

    /// <summary>The most undelivered events of one module a sweep hands on; the next sweep takes the rest.</summary>
    public int SweepBatch { get; set; } = 1000;
}

/// <summary>
/// Hands committed events to every handler in this program, right after the commit, through a fixed
/// number of delivery workers (and what a sweep finds through workers of its own). A handler that fails is retried with growing, jittered waits, then left
/// undelivered with its error recorded; the row is marked delivered only when every handler has
/// succeeded. A sweep every minute delivers again whatever has stayed undelivered, and the first
/// sweep, at start, takes every undelivered row; it waits and tries again while the database cannot
/// be reached, so a task that starts during an outage keeps running. Stopping, it takes no new event
/// and finishes the ones in hand; the rest wait for a sweep.
/// </summary>
public sealed partial class OutboxDispatcher(
    EventBus bus,
    IEnumerable<IModuleEvents> modules,
    IEnumerable<EventHandlerRegistration> handlers,
    IEnumerable<PublishedEvent> published,
    EventRoutes routes,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService, IStopParticipant
{
    public const string MeterName = "CrowdFunding.Outbox";

    /// <summary>The source of the spans that hand events to handlers.</summary>
    public const string ActivitySourceName = "CrowdFunding.Events";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    private readonly Dictionary<string, Type> _knownTypes = handlers.Select(h => h.EventType)
        .Concat(published.Select(p => p.EventType))
        .Distinct()
        .ToDictionary(t => t.Name);

    // A sweep claims only rows a handler here can take: a row claimed by a program without its consumer would wait out the claim.
    private readonly string[] _handledTypes = [.. handlers.Select(h => h.EventType.Name).Distinct()];

    private readonly CancellationTokenSource _taking = new();

    // What sweeps found, in a queue of its own: a worker takes from it only when no fresh event waits,
    // so a backlog after an outage never holds back the events being committed now.
    private readonly Channel<OutboxRecord> _redeliveries = Channel.CreateUnbounded<OutboxRecord>();

    // Events a sweep has queued or a worker is delivering: a sweep does not queue them again.
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    private readonly ConcurrentDictionary<Guid, byte> _delivering = new();

    private double _oldestUndeliveredSeconds;

    public string Name => "outbox dispatcher";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Meter.CreateObservableGauge("outbox.oldest_undelivered_age", () => _oldestUndeliveredSeconds, "s",
            "Age of the oldest event not yet delivered to every handler");
        var workers = Enumerable.Range(0, Math.Max(1, options.Value.MaxConcurrentDeliveries)).Select(_ => Work(stoppingToken));
        await Task.WhenAll([Sweep(stoppingToken), .. workers]);
    }

    /// <summary>Takes no new event, and waits for the deliveries in hand to finish (their retries are cut short).</summary>
    public async Task Stop(CancellationToken cancellationToken)
    {
        await _taking.CancelAsync();
        while (!_delivering.IsEmpty)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    public override void Dispose()
    {
        _taking.Dispose();
        base.Dispose();
    }

    private async Task Work(CancellationToken stoppingToken)
    {
        using var taking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _taking.Token);
        try
        {
            while (!taking.IsCancellationRequested)
            {
                if (bus.Reader.TryRead(out var record) || _redeliveries.Reader.TryRead(out record))
                {
                    await DeliverOnce(record, stoppingToken);
                    continue;
                }

                // Nothing waits: sleep until either queue has something. The wait left over is cancelled.
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(taking.Token);
                var woken = await Task.WhenAny(
                    bus.Reader.WaitToReadAsync(waiting.Token).AsTask(),
                    _redeliveries.Reader.WaitToReadAsync(waiting.Token).AsTask());
                await waiting.CancelAsync();
                await woken;
            }
        }
        catch (OperationCanceledException)
        {
            // stopping: what is left in the queue stays undelivered for a sweep
        }
    }

    /// <summary>One delivery at a time per event in this task; its faults are logged here, and the row stays for a sweep.</summary>
    private async Task DeliverOnce(OutboxRecord record, CancellationToken cancellationToken)
    {
        if (!_delivering.TryAdd(record.EventId, 0))
        {
            return;
        }

        try
        {
            await Deliver(record, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogDeliveryFailed(logger, e, record.Type, record.EventId);
        }
        finally
        {
            _delivering.TryRemove(record.EventId, out _);
            _queued.TryRemove(record.EventId, out _);
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

        // The delivery continues the trace of the change that wrote the event, even after a restart.
        using var activity = Source.StartActivity($"deliver {record.Type}", ActivityKind.Consumer, record.TraceParent);
        activity?.SetTag("messaging.system", "in-process");
        activity?.SetTag("messaging.message.id", record.EventId.ToString());
        activity?.SetTag("crowdfunding.module", record.Module);
        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(record.Payload, type, EventBus.Json)!;
        var source = modules.Single(m => m.Module == record.Module);
        // The modules the row owes it to; a row from before routes existed owes it to every consumer here.
        var owedTo = record.Sinks?.Where(s => s != OutboxSink.Broker).ToList();
        if (owedTo is { Count: 0 })
        {
            return; // nothing owed in process
        }

        if (owedTo?.FirstOrDefault(m => !routes.RunsHere(m)) is { } elsewhere)
        {
            // Written by a program that runs a module this one does not: left for a program that does.
            LogOwedElsewhere(logger, record.Type, record.EventId, elsewhere);
            return;
        }

        // The row's sinks decide, not today's routes: a row written in process stays owed in process after
        // its consumer's route turns to Broker (it was never published), and a row from before routes is
        // owed to every consumer here.
        var inProcess = handlers.Where(h => h.EventType == type && (owedTo is null || owedTo.Contains(h.Module))).ToList();
        if (inProcess.Count == 0)
        {
            // Nothing here received it, so it is not delivered: the row waits for a program that has its consumers.
            LogNoConsumerHere(logger, record.Type, record.EventId);
            return;
        }

        var allHandled = true;
        foreach (var handler in inProcess)
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
                using var handling = Source.StartActivity($"handle {handler.Handler}", ActivityKind.Internal);
                await target.HandleOnce(handler.Handler, record.EventId,
                    (scope, ct) => handler.Invoke(scope, integrationEvent, ct), cancellationToken);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await TryRecordFailure(source, record, $"{handler.Handler}: {e.Message}", cancellationToken);
                if (attempt >= delays.Count || _taking.IsCancellationRequested)
                {
                    LogGaveUp(logger, e, handler.Handler, record.Type, record.EventId);
                    return false;
                }

                try
                {
                    await Task.Delay(Jittered(delays[attempt]), _taking.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return false; // stopping: the row waits for a sweep
                }
            }
        }
    }

    /// <summary>Between half and all of the wait, at random.</summary>
    private static TimeSpan Jittered(TimeSpan wait) => wait * (0.5 + (Random.Shared.NextDouble() / 2));

    /// <summary>
    /// Records why a handler failed. When the database is the cause, this fails too; that is logged,
    /// and the row simply stays undelivered for a sweep.
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

    /// <summary>
    /// The first sweep hands on every undelivered row, whatever its age: the rows of a task that
    /// stopped. Until it has reached every module it is tried again, after waits that double up to
    /// <see cref="OutboxOptions.SweepEvery"/>. From then on, each sweep hands on the rows older than
    /// <see cref="OutboxOptions.RedeliverAfter"/>.
    /// </summary>
    private async Task Sweep(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var wait = settings.RetryDelays.Count > 0 ? settings.RetryDelays[0] : TimeSpan.FromSeconds(1);
        var started = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var swept = await SweepOnce(started ? DateTimeOffset.UtcNow - settings.RedeliverAfter : null, stoppingToken);
                if (!started && !swept)
                {
                    await Task.Delay(Jittered(wait), stoppingToken);
                    wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, settings.SweepEvery.Ticks));
                    continue;
                }

                started = true;
                await Task.Delay(settings.SweepEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    /// <summary>True when every module was read; a module that cannot be reached is logged and tried on the next sweep.</summary>
    private async Task<bool> SweepOnce(DateTimeOffset? occurredBefore, CancellationToken cancellationToken)
    {
        var all = true;
        var oldest = 0.0;
        foreach (var module in modules)
        {
            try
            {
                oldest = Math.Max(oldest, (await module.OldestUndeliveredAge(cancellationToken))?.TotalSeconds ?? 0);
                foreach (var record in await module.Undelivered(occurredBefore, _handledTypes, options.Value.SweepBatch, options.Value.RedeliverAfter, cancellationToken))
                {
                    if (!_delivering.ContainsKey(record.EventId) && _queued.TryAdd(record.EventId, 0))
                    {
                        _redeliveries.Writer.TryWrite(record);
                    }
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // The database has not been migrated yet; there is nothing to deliver.
                LogNotMigrated(logger, module.Module);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogSweepFailed(logger, e, module.Module);
                all = false;
            }
        }

        _oldestUndeliveredSeconds = oldest;
        return all;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No handler in this program knows {Type}; event {EventId} stays undelivered")]
    private static partial void LogUnknownType(ILogger logger, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Type} {EventId} is owed to {Module}, which does not run here; left for a program that runs it")]
    private static partial void LogOwedElsewhere(ILogger logger, string type, Guid eventId, string module);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No consumer of {Type} runs in this program; event {EventId} stays undelivered")]
    private static partial void LogNoConsumerHere(ILogger logger, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Handler} failed on {Type} {EventId} after every retry; it stays undelivered for a sweep")]
    private static partial void LogGaveUp(ILogger logger, Exception exception, string handler, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Delivering {Type} {EventId} failed; the row stays undelivered for a sweep")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception exception, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record a failure for {Type} {EventId}; the row stays undelivered for a sweep")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string type, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Module} outbox does not exist yet; run migrate")]
    private static partial void LogNotMigrated(ILogger logger, string module);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not sweep the {Module} outbox; the next sweep tries again")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception, string module);
}
