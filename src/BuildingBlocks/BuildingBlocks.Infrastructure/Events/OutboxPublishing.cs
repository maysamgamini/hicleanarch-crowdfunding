using System.Threading.Channels;

using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure.Events;

public sealed class OutboxPublishingOptions
{
    /// <summary>
    /// Rows a task holds in memory on their way to the broker. A commit never waits for room: when the
    /// queue is full the row stays unpublished, and the recovery pass sends it.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>How often the recovery pass runs (and once at start).</summary>
    public TimeSpan RecoverEvery { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Younger unpublished rows belong to the task that wrote them; recovery takes only older ones.</summary>
    public TimeSpan RecoverAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most rows one claim takes; the pass claims again until none is left.</summary>
    public int RecoveryBatch { get; set; } = 1000;

    /// <summary>How long a claim holds its rows: another task's recovery skips them meanwhile.</summary>
    public TimeSpan Claim { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// A module whose events stop moving for a while, during a data move (lesson s4-l07): its rows are not
/// published and its queue is not read until it resumes. Nothing is lost; the rows and messages wait.
/// A program without one never pauses.
/// </summary>
public interface IEventFlowPause
{
    Task<bool> IsPaused(string module, CancellationToken cancellationToken);
}

/// <summary>Committed rows that owe a publish, on their way from the commit to the publisher. Bounded.</summary>
public sealed class PublishHandoff(IOptions<OutboxPublishingOptions> options)
{
    private readonly Channel<OutboxRecord> _channel = Channel.CreateBounded<OutboxRecord>(
        new BoundedChannelOptions(options.Value.QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>Never waits: when the queue is full the row is left for recovery.</summary>
    public void Offer(OutboxRecord record) => _channel.Writer.TryWrite(record);

    internal ChannelReader<OutboxRecord> Reader => _channel.Reader;
}

/// <summary>
/// Hands committed rows to the broker right after the commit, in batches, and marks what the broker
/// accepted. A recovery pass every minute (and at start) claims the unpublished rows older than half a
/// minute, so a row whose task stopped, or that the queue had no room for, is published by some task,
/// once claimed, never by every task at once. Stopping, it takes no new row; the rest wait for recovery.
/// </summary>
public sealed partial class OutboxPublisher(
    PublishHandoff queue,
    IEventPublisher publisher,
    IEnumerable<IModuleEvents> modules,
    IEnumerable<IEventFlowPause> pauses,
    IOptions<OutboxPublishingOptions> options,
    ILogger<OutboxPublisher> logger) : BackgroundService, IStopParticipant
{
    private const int Batch = 100;

    private readonly CancellationTokenSource _taking = new();

    public string Name => "outbox publisher";

    // The age of the oldest unpublished row is measured by the dispatcher's sweep, which every program
    // runs, so the alarm on it does not depend on this publisher running.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Drain(stoppingToken), Recover(stoppingToken));

    public async Task Stop(CancellationToken cancellationToken) => await _taking.CancelAsync();

    public override void Dispose()
    {
        _taking.Dispose();
        base.Dispose();
    }

    /// <summary>Publishes what the commits handed on, a batch at a time.</summary>
    private async Task Drain(CancellationToken stoppingToken)
    {
        using var taking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _taking.Token);
        try
        {
            while (await queue.Reader.WaitToReadAsync(taking.Token))
            {
                var batch = new List<OutboxRecord>(Batch);
                while (batch.Count < Batch && queue.Reader.TryRead(out var record))
                {
                    batch.Add(record);
                }

                await PublishAndMark(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping: what is left in memory is still unpublished in the database, for recovery
        }
    }

    /// <summary>Claims and publishes old unpublished rows, module by module, until none is left; then waits.</summary>
    private async Task Recover(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var module in modules)
                {
                    try
                    {
                        IReadOnlyList<OutboxRecord> claimed;
                        do
                        {
                            claimed = await module.ClaimUnpublished(settings.RecoverAfter, settings.RecoveryBatch, settings.Claim, stoppingToken);
                            foreach (var chunk in claimed.Chunk(Batch))
                            {
                                await PublishAndMark(chunk, stoppingToken);
                            }
                        }
                        while (claimed.Count == settings.RecoveryBatch && !_taking.IsCancellationRequested);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        LogRecoveryFailed(logger, e, module.Module);
                    }
                }

                await Task.Delay(settings.RecoverEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task PublishAndMark(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            // A paused module's rows stay unpublished; recovery sends them once it resumes.
            var paused = new HashSet<string>();
            foreach (var module in records.Select(r => r.Module).Distinct())
            {
                foreach (var pause in pauses)
                {
                    if (await pause.IsPaused(module, cancellationToken))
                    {
                        paused.Add(module);
                    }
                }
            }

            records = [.. records.Where(r => !paused.Contains(r.Module))];
            if (records.Count == 0)
            {
                return;
            }

            var accepted = await publisher.Publish(records, cancellationToken);
            foreach (var byModule in records.Where(r => accepted.Contains(r.EventId)).GroupBy(r => r.Module))
            {
                await modules.Single(m => m.Module == byModule.Key).MarkPublished([.. byModule.Select(r => r.EventId)], cancellationToken);
            }

            if (accepted.Count < records.Count)
            {
                LogPartlyPublished(logger, records.Count - accepted.Count, records.Count);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nothing marked: every row stays unpublished, and recovery sends it. A repeat is harmless (inbox).
            LogPublishFailed(logger, e, records.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The broker took {Missing} of {Total} events only partly; the rest wait for recovery")]
    private static partial void LogPartlyPublished(ILogger logger, int missing, int total);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing {Count} events failed; they wait for recovery")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not recover the {Module} outbox's unpublished events; the next pass tries again")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception, string module);
}
