namespace CrowdFunding.Messaging;

/// <summary>The attributes every event carries, and the limit a raw-delivery subscription imposes.</summary>
public static class MessageAttributes
{
    public const string EventType = "event_type";
    public const string EventVersion = "event_version";
    public const string Producer = "producer";
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";

    /// <summary>SNS discards a message with more than ten attributes on a raw-delivery SQS subscription.</summary>
    public const int Max = 10;

    /// <summary>Refuses a message that SNS would silently drop.</summary>
    public static void Guard(IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count > Max)
        {
            throw new InvalidOperationException(
                $"A message may carry at most {Max} attributes; this one has {attributes.Count}: {string.Join(", ", attributes.Keys)}.");
        }
    }
}
