using Amazon.SQS.Model;

namespace CrowdFunding.Messaging;

/// <summary>The message attributes of one event, on the way out and on the way in.</summary>
public static class EventAttributes
{
    /// <summary>
    /// An outbox row's attributes: its type, version and producer, and the context of the change that
    /// wrote it. Five at most, checked against the ten SNS allows on a raw-delivery subscription.
    /// </summary>
    public static IReadOnlyDictionary<string, string> For(string eventType, int version, string producer, string? traceParent, string? traceState = null)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageAttributes.EventType] = eventType,
            [MessageAttributes.EventVersion] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [MessageAttributes.Producer] = producer,
        };
        MessageTraces.Inject(attributes, traceParent, traceState);
        MessageAttributes.Guard(attributes);
        return attributes;
    }

    /// <summary>A received message's string attributes. The receive must ask for them (<c>MessageAttributeNames = ["All"]</c>).</summary>
    public static IReadOnlyDictionary<string, string> Of(Message message) =>
        (message.MessageAttributes ?? []).Where(a => a.Value.StringValue is not null)
            .ToDictionary(a => a.Key, a => a.Value.StringValue, StringComparer.Ordinal);
}
