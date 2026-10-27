using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>
/// One structured log line per step of a pledge's journey (lesson s5-l13): an event a module's transaction
/// wrote, once it committed, or an event a handler took, in process or from the service's queue. Each line
/// names the step, the module, the campaign and the pledge it is about (read from the event's
/// <c>campaignId</c> and <c>pledgeId</c>), the event, the trace and the outcome. A campaign is never one
/// trace, because bulk and fan-out events start a trace per message, so the journey view finds a
/// campaign's steps in every service's log group by <c>campaign.id</c>.
/// </summary>
public static class JourneyLog
{
    public static readonly EventId Step = new(7000, "JourneyStep");

    /// <summary>The event's row committed with the change it reports; where it goes next is the outbox's.</summary>
    public const string Committed = "committed";
    public const string Handled = "handled";
    public const string Failed = "failed";

    /// <summary>A module's event left its transaction: the step is the event's type, the module its producer.</summary>
    public static void EventCommitted(ILogger logger, OutboxRecord record) =>
        Write(logger, record.Type, record.Module, record.EventId, record.Type, record.Payload, record.TraceParent, Committed);

    /// <summary>A handler took an event from the outbox, or failed to: the step is the handler, as the inbox names it.</summary>
    public static void EventHandled(ILogger logger, string handler, string module, OutboxRecord record, string outcome) =>
        Write(logger, handler, module, record.EventId, record.Type, record.Payload, record.TraceParent, outcome);

    /// <summary>A handler took an event from the service's queue, or failed to (the SQS reader).</summary>
    public static void EventHandled(ILogger logger, string handler, string module, Guid eventId, string eventType, string payload, string? traceParent, string outcome) =>
        Write(logger, handler, module, eventId, eventType, payload, traceParent, outcome);

    private static void Write(ILogger logger, string step, string module, Guid eventId, string eventType, string payload, string? traceParent, string outcome)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var (campaignId, pledgeId) = IdsOf(payload);
        var traceId = Activity.Current?.TraceId.ToHexString() ?? TraceIdOf(traceParent);
        logger.Log(LogLevel.Information, Step, new Line(step, module, campaignId, pledgeId, eventId, eventType, traceId, outcome), null, (line, _) => line.ToString());
    }

    /// <summary>The ids at the top level of the event's JSON, if it has them.</summary>
    internal static (string? CampaignId, string? PledgeId) IdsOf(string payload)
    {
        string? campaign = null;
        string? pledge = null;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(payload));
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && string.Equals(name, "campaignId", StringComparison.OrdinalIgnoreCase))
                    {
                        campaign = reader.GetString();
                    }
                    else if (reader.TokenType == JsonTokenType.String && string.Equals(name, "pledgeId", StringComparison.OrdinalIgnoreCase))
                    {
                        pledge = reader.GetString();
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // an unreadable payload still gets its line, without ids
        }

        return (campaign, pledge);
    }

    /// <summary>The trace id inside a W3C traceparent (version-traceid-spanid-flags).</summary>
    private static string? TraceIdOf(string? traceParent) =>
        traceParent is { Length: >= 35 } ? traceParent.Substring(3, 32) : null;

    /// <summary>The line's fields, under the names the journey query reads.</summary>
    private sealed class Line(string step, string module, string? campaignId, string? pledgeId, Guid eventId, string eventType, string? traceId, string outcome)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _fields =
        [
            new("journey.step", step),
            new("module", module),
            new("campaign.id", campaignId),
            new("pledge.id", pledgeId),
            new("event.id", eventId),
            new("event.type", eventType),
            new("trace.id", traceId),
            new("outcome", outcome),
            new("{OriginalFormat}", "Journey {journey.step} {outcome} for campaign {campaign.id} pledge {pledge.id}"),
        ];

        public int Count => _fields.Length;

        public KeyValuePair<string, object?> this[int index] => _fields[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_fields).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"Journey {step} {outcome} for campaign {campaignId} pledge {pledgeId}";
    }
}
