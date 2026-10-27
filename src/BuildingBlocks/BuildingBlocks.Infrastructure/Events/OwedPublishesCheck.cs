using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>
/// Refuses to start a program that has no broker publisher while one of its outboxes still holds a row
/// that owes the broker a publish. Such a row is delivered by nothing else: without a publisher it waits
/// for ever, and so do the messages of the queues no reader here takes. This is the program started
/// after its <c>Events:Broker</c> section was dropped; the safe rollback is the routes back to
/// <c>InProcess</c> with the broker section kept until nothing is owed (lesson s4-l02).
/// When the outboxes cannot be read at start, the program starts and the
/// <c>outbox.oldest_unpublished_age</c> gauge, which every program reports, raises the alarm instead.
/// </summary>
internal sealed partial class OwedPublishesCheck(
    IEnumerable<IModuleEvents> modules,
    IEnumerable<IEventPublisher> publishers,
    ILogger<OwedPublishesCheck> logger) : IHostedService
{
    /// <summary>How long the check may hold up the start before it gives way to the alarm.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (publishers.Any())
        {
            return;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);
        var owing = new List<string>();
        foreach (var module in modules)
        {
            try
            {
                if (await module.OldestUnpublishedAge(limit.Token) is { } age)
                {
                    owing.Add($"{module.Module} (the oldest for {age.TotalMinutes:0} min)");
                }
            }
            catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                // Not migrated to an outbox with sinks yet: nothing can owe the broker.
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                LogNotChecked(logger, e, module.Module);
            }
        }

        if (owing.Count > 0)
        {
            throw new InvalidOperationException(
                "This program has no broker publisher (no Events:Broker section), but rows still owe the broker a publish in: "
                + string.Join(", ", owing) + ". Nothing else delivers them. Start it with its Events:Broker section and the routes "
                + "set back to InProcess, and keep that until the outboxes owe nothing and the queues are drained.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the {Module} outbox for rows owed to the broker; the outbox.oldest_unpublished_age alarm watches them")]
    private static partial void LogNotChecked(ILogger logger, Exception exception, string module);
}
