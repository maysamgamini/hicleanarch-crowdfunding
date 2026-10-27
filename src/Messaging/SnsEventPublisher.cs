using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

using BuildingBlocks.Infrastructure.Events;

namespace CrowdFunding.Messaging;

/// <summary>
/// Publishes outbox rows to their producing module's SNS topic, ten to a call (PublishBatch), with at
/// most <see cref="BrokerOptions.MaxCallsInFlight"/> calls in flight per task. The SDK's standard retry
/// mode retries throttling and transient errors with jittered back-off; an entry SNS still refuses
/// stays unpublished, for the recovery pass.
/// </summary>
public sealed class SnsEventPublisher(IAmazonSimpleNotificationService sns, BrokerOptions options) : IEventPublisher, IDisposable
{
    private const int BatchSize = 10; // PublishBatch takes at most ten entries

    private readonly SemaphoreSlim _inFlight = new(options.MaxCallsInFlight);

    public void Dispose() => _inFlight.Dispose();

    public async Task<IReadOnlyCollection<Guid>> Publish(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        var calls = records.GroupBy(r => r.Module)
            .SelectMany(byModule => byModule.Chunk(BatchSize).Select(batch => PublishBatch(byModule.Key, batch, cancellationToken)));
        var accepted = await Task.WhenAll(calls);
        return [.. accepted.SelectMany(a => a)];
    }

    /// <summary>The attributes a message carries: what the filter policies and the reader need, and the trace.</summary>
    public static Dictionary<string, string> AttributesOf(OutboxRecord record)
    {
        var attributes = new Dictionary<string, string>
        {
            [MessageAttributes.EventType] = record.Type,
            [MessageAttributes.EventVersion] = record.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [MessageAttributes.Producer] = record.Module,
        };
        if (record.TraceParent is { } traceParent)
        {
            attributes[MessageAttributes.TraceParent] = traceParent;
        }

        MessageAttributes.Guard(attributes);
        return attributes;
    }

    private async Task<IEnumerable<Guid>> PublishBatch(string module, OutboxRecord[] batch, CancellationToken cancellationToken)
    {
        if (!options.Topics.TryGetValue(module, out var topicArn))
        {
            throw new InvalidOperationException($"No topic is configured for {module} (Events:Broker:Topics:{module}).");
        }

        await _inFlight.WaitAsync(cancellationToken);
        try
        {
            var response = await sns.PublishBatchAsync(new PublishBatchRequest
            {
                TopicArn = topicArn,
                PublishBatchRequestEntries = [.. batch.Select(r => new PublishBatchRequestEntry
                {
                    Id = r.EventId.ToString("N"),
                    Message = r.Payload,
                    MessageAttributes = AttributesOf(r).ToDictionary(
                        a => a.Key, a => new MessageAttributeValue { DataType = "String", StringValue = a.Value }),
                })],
            }, cancellationToken);
            return (response.Successful ?? []).Select(s => Guid.ParseExact(s.Id, "N"));
        }
        finally
        {
            _inFlight.Release();
        }
    }
}
