using System.Diagnostics;

namespace CrowdFunding.Messaging;

/// <summary>
/// W3C trace context across the broker. SNS and SQS carry no trace of ours, so the publisher puts the
/// context that wrote an event into two of its message attributes, and the reader starts one processing
/// span per message and handler run from them. That span always links to the creation context, as the
/// OpenTelemetry messaging conventions ask (they are still in Development status). Whether it is also
/// a child of it is this instrumentation's choice, made per event type: see <see cref="ContinueOneStory"/>.
/// </summary>
public static class MessageTraces
{
    public const string ActivitySourceName = "CrowdFunding.Messaging";

    /// <summary>The linked trace's id, for a backend that does not draw links.</summary>
    public const string LinkedTraceId = "crowdfunding.linked_trace_id";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    /// <summary>
    /// The events that continue one story about one pledge: a card saved, a charge's result. Their
    /// handling is a child of the change that wrote them, so the story reads as one trace. Every other
    /// event is written in bulk or fanned out (one close pass writes up to a million
    /// <c>PledgeChargeRequested</c> in one trace; <c>CampaignFundingDecided</c>, <c>CampaignPublished</c>
    /// and <c>CampaignSettled</c> reach several modules) and is handled in a new trace per message,
    /// linked to the one that wrote it, so no trace holds a million handler spans. An event type not
    /// named here gets a new trace: the safe side.
    /// </summary>
    public static readonly IReadOnlySet<string> ContinueOneStory = new HashSet<string>(StringComparer.Ordinal)
    {
        "PaymentMethodSaved",
        "PledgeChargeSucceeded",
        "PledgeChargeFailed",
    };

    /// <summary>
    /// Adds the creation context to a message's attributes: <c>traceparent</c>, and <c>tracestate</c>
    /// when there is one. Two of the ten attributes a raw-delivery subscription allows; a context that
    /// does not parse is left out rather than sent.
    /// </summary>
    public static void Inject(IDictionary<string, string> attributes, string? traceParent, string? traceState = null)
    {
        if (string.IsNullOrEmpty(traceParent) || !ActivityContext.TryParse(traceParent, traceState, out _))
        {
            return;
        }

        attributes[MessageAttributes.TraceParent] = traceParent;
        if (!string.IsNullOrEmpty(traceState))
        {
            attributes[MessageAttributes.TraceState] = traceState;
        }
    }

    /// <summary>The creation context a message carries, or null when it carries none that parses.</summary>
    public static ActivityContext? Extract(IReadOnlyDictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(MessageAttributes.TraceParent, out var traceParent))
        {
            return null;
        }

        attributes.TryGetValue(MessageAttributes.TraceState, out var traceState);
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context) ? context : null;
    }

    /// <summary>
    /// Starts the span of one handler's run on one message. <paramref name="received"/> is the span that
    /// received the batch, if any. Disposing the result ends the span and makes the caller's span current again.
    /// </summary>
    /// <summary>A queue's name, the last segment of its URL or ARN, as messaging.destination.name wants it.</summary>
    public static string QueueName(string urlOrArn) => urlOrArn.TrimEnd('/').Split('/', ':')[^1];

    public static ProcessingSpan StartProcessing(
        string eventType, string handler, IReadOnlyDictionary<string, string> attributes, ActivityContext received = default,
        string? messageId = null, string? queue = null)
    {
        var created = Extract(attributes);
        var child = created is not null && ContinueOneStory.Contains(eventType);
        var links = new List<ActivityLink>();
        if (created is { } c)
        {
            links.Add(new ActivityLink(c));
        }

        if (child && received != default)
        {
            links.Add(new ActivityLink(received));
        }

        var tags = new List<KeyValuePair<string, object?>>
        {
            new("messaging.system", "aws_sqs"),
            new("messaging.operation.type", "process"),
            new("messaging.operation.name", "process"),
            new("messaging.destination.name", queue),
            new("messaging.message.id", messageId),
            new("crowdfunding.event_type", eventType),
            new("crowdfunding.handler", handler),
            new("crowdfunding.trace_shape", child ? "child" : "new_trace"),
        };
        if (created is { } linked)
        {
            tags.Add(new(LinkedTraceId, linked.TraceId.ToString()));
        }

        var previous = Activity.Current;
        if (!child)
        {
            // A new trace: with no current span, a span started without a parent is a root.
            Activity.Current = null;
        }

        var activity = Source.StartActivity($"process {eventType}", ActivityKind.Consumer, child ? created!.Value : default, tags, links);
        if (activity is null)
        {
            Activity.Current = previous;
        }

        return new ProcessingSpan(activity, previous);
    }
}

/// <summary>A handler's span over one message, and the span that was current before it.</summary>
public sealed class ProcessingSpan(Activity? activity, Activity? previous) : IDisposable
{
    public Activity? Span => activity;

    public void Dispose()
    {
        activity?.Dispose();
        Activity.Current = previous;
    }
}
