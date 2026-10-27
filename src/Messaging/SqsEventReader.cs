using System.Diagnostics;
using System.Text.Json;

using Amazon.SQS;
using Amazon.SQS.Model;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Hosting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrowdFunding.Messaging;

/// <summary>
/// Reads this host's SQS queues and hands each event to every handler here of a module the queue serves
/// that consumes it, each in its own inbox transaction. What a queued message owes was decided when it was
/// published, so today's routes do not filter it: after a route goes back to <c>InProcess</c>, the messages
/// already queued are still handled (a repeat does nothing, by the inbox). A message is deleted only after every
/// handler committed; a message that fails stays, hidden for longer each time it comes back, and after
/// five receives the queue moves it to its dead-letter queue. No retry in memory: the queue is the retry.
/// </summary>
public sealed partial class SqsEventReader(
    IAmazonSQS sqs,
    BrokerOptions options,
    BrokerPlan plan,
    EventRoutes routes,
    IEnumerable<EventHandlerRegistration> handlers,
    IEnumerable<IEventFlowPause> pauses,
    IEnumerable<IModuleEvents> modules,
    ILogger<SqsEventReader> logger) : BackgroundService, IStopParticipant
{
    private static readonly ActivitySource Source = new(MessageTraces.ActivitySourceName);

    private readonly SemaphoreSlim _handling = new(options.MaxConcurrentMessages);

    private readonly CancellationTokenSource _taking = new();

    public string Name => "SQS event reader";

    /// <summary>
    /// Reads each configured queue whose service has a module running here. A queue none of whose
    /// modules run here is left to the host that runs them: the monolith with Payments in its own service
    /// (s4-l05) must not take Payments' messages, which it could only fail into the dead-letter queue.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Read
            .Where(service => plan.Services.TryGetValue(service, out var modules) && modules.Any(routes.RunsHere))
            .Select(service => Read(service, stoppingToken)));

    public async Task Stop(CancellationToken cancellationToken)
    {
        await _taking.CancelAsync();
        // Wait for the messages in hand; the rest stay in the queue for another task.
        for (var i = 0; i < options.MaxConcurrentMessages; i++)
        {
            await _handling.WaitAsync(cancellationToken);
        }
    }

    public override void Dispose()
    {
        _taking.Dispose();
        _handling.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Receives no more messages than this task can start handling now (a slot held for the first, and
    /// those free when the receive goes out), and keeps every received message invisible to other readers
    /// from the moment it arrives until it is deleted or hidden on purpose: the slot it may wait for, and
    /// handlers that run one after another, never let its visibility timeout run out while it is in hand.
    /// </summary>
    private async Task Read(string service, CancellationToken stoppingToken)
    {
        var queueUrl = options.Queues[service].Url;
        using var taking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _taking.Token);
        var inHand = new List<Task>();
        while (!taking.IsCancellationRequested)
        {
            inHand.RemoveAll(t => t.IsCompleted);
            try
            {
                if (await Paused(service, taking.Token))
                {
                    // A data move holds the queue's modules (s4-l07): the messages wait in the queue.
                    await Task.Delay(TimeSpan.FromSeconds(5), taking.Token);
                    continue;
                }

                await _handling.WaitAsync(taking.Token);
            }
            catch (OperationCanceledException) when (taking.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogReceiveFailed(logger, e, queueUrl);
                continue;
            }

            var slotHeld = true;
            List<InHand> received = [];
            var started = 0;
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = Math.Min(10, 1 + _handling.CurrentCount),
                    WaitTimeSeconds = options.WaitSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }, taking.Token);
                received = [.. (response.Messages ?? []).Select(m => new InHand(sqs, logger, TimeSpan.FromSeconds(options.VisibilityTimeoutSeconds), queueUrl, m, stoppingToken))];
                if (received.Count == 0)
                {
                    continue;
                }

                // One span for the batch; each handler's run on each message gets its own (s4-l08).
                using var receive = Source.StartActivity($"receive {service}", ActivityKind.Consumer);
                receive?.SetTag("messaging.system", "aws_sqs");
                receive?.SetTag("messaging.operation.type", "receive");
                receive?.SetTag("messaging.destination.name", service);
                receive?.SetTag("messaging.batch.message_count", received.Count);
                var context = receive?.Context ?? default;
                foreach (var message in received)
                {
                    if (!slotHeld)
                    {
                        await _handling.WaitAsync(taking.Token); // another queue took the slot meanwhile; this one stays invisible
                    }

                    slotHeld = false;
                    inHand.Add(HandleAndRelease(service, message, context, stoppingToken));
                    started++;
                }
            }
            catch (OperationCanceledException) when (taking.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogReceiveFailed(logger, e, queueUrl);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), taking.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (slotHeld)
                {
                    _handling.Release();
                }

                // Received but not started (the task is stopping): no longer kept invisible, so they come back for another task.
                foreach (var message in received.Skip(started))
                {
                    await message.DisposeAsync();
                }
            }
        }

        await Task.WhenAll(inHand);
    }

    private async Task<bool> Paused(string service, CancellationToken cancellationToken)
    {
        foreach (var module in plan.Services[service].Where(routes.RunsHere))
        {
            foreach (var pause in pauses)
            {
                if (await pause.IsPaused(module, cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task HandleAndRelease(string service, InHand inHand, ActivityContext received, CancellationToken cancellationToken)
    {
        var message = inHand.Message;
        try
        {
            var handled = await Handle(service, message, cancellationToken, received);
            await inHand.DisposeAsync(); // no more extensions: the delete, or the wait set below, is the last word
            if (handled)
            {
                await sqs.DeleteMessageAsync(inHand.QueueUrl, message.ReceiptHandle, cancellationToken);
            }
            else
            {
                await HideFor(inHand.QueueUrl, message, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHandleFailed(logger, e, message.MessageId);
            await inHand.DisposeAsync();
            await HideFor(inHand.QueueUrl, message, cancellationToken);
        }
        finally
        {
            await inHand.DisposeAsync();
            _handling.Release();
        }
    }

    /// <summary>
    /// A received message, kept invisible to other readers: every third of the visibility timeout, its
    /// visibility is set to a whole timeout again, until it is disposed.
    /// </summary>
    private sealed class InHand : IAsyncDisposable
    {
        private readonly CancellationTokenSource _keeping;
        private readonly Task _extending;
        private int _disposed;

        public InHand(IAmazonSQS sqs, ILogger logger, TimeSpan timeout, string queueUrl, Message message, CancellationToken cancellationToken)
        {
            QueueUrl = queueUrl;
            Message = message;
            _keeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _extending = Extend(sqs, logger, timeout, _keeping.Token);
        }

        public string QueueUrl { get; }

        public Message Message { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _keeping.CancelAsync();
            await _extending;
            _keeping.Dispose();
        }

        private async Task Extend(IAmazonSQS sqs, ILogger logger, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(timeout / 3, cancellationToken);
                    try
                    {
                        await sqs.ChangeMessageVisibilityAsync(QueueUrl, Message.ReceiptHandle, (int)timeout.TotalSeconds, cancellationToken);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        LogExtendFailed(logger, e, Message.MessageId); // tried again at the next third
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // disposed: handled, hidden on purpose, or the task is stopping
            }
        }
    }

    /// <summary>True when every handler here of a module <paramref name="service"/>'s queue serves, consuming this event, has handled it.</summary>
    public async Task<bool> Handle(string service, Message message, CancellationToken cancellationToken, ActivityContext received = default)
    {
        var type = Attribute(message, MessageAttributes.EventType);
        var producer = Attribute(message, MessageAttributes.Producer);
        if (type is null || producer is null || !routes.HandledTypes().TryGetValue(type, out var eventType))
        {
            LogUnknown(logger, message.MessageId, type ?? "?");
            return false;
        }

        var served = plan.Services[service];
        var consumers = routes.Subscriptions.ConsumersOf(type);
        var owed = handlers.Where(h => h.EventType == eventType && served.Contains(h.Module) && consumers.Contains(h.Module)).ToList();
        if (owed.Count == 0)
        {
            LogNotForHere(logger, type, producer, message.MessageId);
            return false;
        }

        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(message.Body, eventType, ContractJson.Options)!;
        var attributes = EventAttributes.Of(message);
        foreach (var handler in owed)
        {
            // A child of the change that wrote it for one pledge's story, a new linked trace otherwise (MessageTraces).
            using var span = MessageTraces.StartProcessing(type, handler.Handler, attributes, received, message.MessageId, service);
            var target = modules.Single(m => m.Module == handler.Module);
            await target.HandleOnce(handler.Handler, integrationEvent.EventId,
                (scope, ct) => handler.Invoke(scope, integrationEvent, ct), cancellationToken);
        }

        return true;
    }

    private async Task HideFor(string queueUrl, Message message, CancellationToken cancellationToken)
    {
        var received = message.Attributes?.TryGetValue("ApproximateReceiveCount", out var count) == true
            && int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 1;
        var wait = options.Waits[Math.Clamp(received - 1, 0, options.Waits.Count - 1)];
        try
        {
            await sqs.ChangeMessageVisibilityAsync(queueUrl, message.ReceiptHandle, (int)wait.TotalSeconds, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHideFailed(logger, e, message.MessageId); // it reappears after the queue's own visibility timeout
        }
    }

    private static string? Attribute(Message message, string name) =>
        message.MessageAttributes?.TryGetValue(name, out var value) == true ? value.StringValue : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} is no event this host knows ({Type}); it stays for the dead-letter queue")]
    private static partial void LogUnknown(ILogger logger, string messageId, string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "No module here that the queue serves consumes {Type} from {Producer}; message {MessageId} stays for the dead-letter queue")]
    private static partial void LogNotForHere(ILogger logger, string type, string producer, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling message {MessageId} failed; it comes back later")]
    private static partial void LogHandleFailed(ILogger logger, Exception exception, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delay message {MessageId}; it comes back after the queue's visibility timeout")]
    private static partial void LogHideFailed(ILogger logger, Exception exception, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not keep message {MessageId} hidden while it is handled; another reader may get it too, and the inbox ignores the copy")]
    private static partial void LogExtendFailed(ILogger logger, Exception exception, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receiving from {QueueUrl} failed; trying again")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception, string queueUrl);
}
