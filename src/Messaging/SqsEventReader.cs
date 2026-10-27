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
    IEnumerable<IModuleEvents> modules,
    ILogger<SqsEventReader> logger) : BackgroundService, IStopParticipant
{
    /// <summary>
    /// Events read from a queue are read strictly: a missing required field or a null where none is
    /// allowed fails the handler, and the message reaches the dead-letter queue instead of being handled with a default.
    /// </summary>
    public static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly ActivitySource Source = new(OutboxDispatcher.ActivitySourceName);

    private readonly SemaphoreSlim _handling = new(options.MaxConcurrentMessages);

    private readonly CancellationTokenSource _taking = new();

    public string Name => "SQS event reader";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Read.Select(service => Read(service, stoppingToken)));

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

    private async Task Read(string service, CancellationToken stoppingToken)
    {
        var queueUrl = options.Queues[service].Url;
        using var taking = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _taking.Token);
        while (!taking.IsCancellationRequested)
        {
            try
            {
                var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = options.WaitSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }, taking.Token);
                var handling = new List<Task>();
                foreach (var message in received.Messages ?? [])
                {
                    await _handling.WaitAsync(taking.Token);
                    handling.Add(HandleAndRelease(service, queueUrl, message, stoppingToken));
                }

                await Task.WhenAll(handling);
            }
            catch (OperationCanceledException) when (taking.IsCancellationRequested)
            {
                return;
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
                    return;
                }
            }
        }
    }

    private async Task HandleAndRelease(string service, string queueUrl, Message message, CancellationToken cancellationToken)
    {
        try
        {
            if (await Handle(service, message, cancellationToken))
            {
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, cancellationToken);
            }
            else
            {
                await HideFor(queueUrl, message, cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogHandleFailed(logger, e, message.MessageId);
            await HideFor(queueUrl, message, cancellationToken);
        }
        finally
        {
            _handling.Release();
        }
    }

    /// <summary>True when every handler here of a module <paramref name="service"/>'s queue serves, consuming this event, has handled it.</summary>
    public async Task<bool> Handle(string service, Message message, CancellationToken cancellationToken)
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

        using var activity = Source.StartActivity($"receive {type}", ActivityKind.Consumer, Attribute(message, MessageAttributes.TraceParent));
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.message.id", message.MessageId);
        var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(message.Body, eventType, StrictJson)!;
        foreach (var handler in owed)
        {
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receiving from {QueueUrl} failed; trying again")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception, string queueUrl);
}
