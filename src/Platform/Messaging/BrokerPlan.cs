using System.Text.Encodings.Web;
using System.Text.Json;

namespace CrowdFunding.Messaging;

/// <summary>One SNS subscription: a producing module's topic to a service's queue, filtered on the event types that service consumes.</summary>
public sealed record BrokerSubscription(string Topic, string Queue, IReadOnlyList<string> EventTypes)
{
    /// <summary>The filter policy on the <c>event_type</c> message attribute (the default scope).</summary>
    public string FilterPolicy => JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<string>> { [MessageAttributes.EventType] = EventTypes });
}

/// <summary>
/// The topics, queues and subscriptions that <c>events/subscriptions.json</c> implies: a topic per
/// producing module, a queue per service, and a subscription wherever a service consumes events a module
/// of another service produces. Rendered to <c>events/broker-subscriptions.json</c>, which OpenTofu reads;
/// the hosts' subscription check and the tests use the same plan.
/// </summary>
public sealed class BrokerPlan(IReadOnlyDictionary<string, IReadOnlyList<string>> services, IReadOnlyList<(string Type, string Producer, IReadOnlyList<string> Consumers)> events)
{
    // Readable in review: quotes inside the filter policies stay quotes. The output is a file, never HTML.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Services => services;

    /// <summary>The service that runs <paramref name="module"/>.</summary>
    public string ServiceOf(string module) =>
        services.FirstOrDefault(s => s.Value.Contains(module)).Key
        ?? throw new InvalidOperationException($"No service in events/subscriptions.json runs {module}.");

    /// <summary>Every producing module: one topic each.</summary>
    public IReadOnlyList<string> Topics => [.. events.Select(e => e.Producer).Distinct().Order(StringComparer.Ordinal)];

    public IReadOnlyList<BrokerSubscription> Subscriptions =>
    [
        .. from producer in Topics
           from service in services.Keys.Order(StringComparer.Ordinal)
           where !services[service].Contains(producer)
           let types = events.Where(e => e.Producer == producer && e.Consumers.Any(services[service].Contains))
               .Select(e => e.Type).Order(StringComparer.Ordinal).ToList()
           where types.Count > 0
           select new BrokerSubscription(producer, service, types),
    ];

    public static BrokerPlan Load(string path)
    {
        using var file = File.OpenRead(path);
        using var document = JsonDocument.Parse(file);
        var root = document.RootElement;
        var services = root.GetProperty("services").EnumerateObject().ToDictionary(
            s => s.Name, s => (IReadOnlyList<string>)[.. s.Value.EnumerateArray().Select(m => m.GetString()!)]);
        var events = root.GetProperty("events").EnumerateObject().Select(e => (
            e.Name,
            e.Value.GetProperty("producer").GetString()!,
            (IReadOnlyList<string>)[.. e.Value.GetProperty("consumers").EnumerateArray().Select(m => m.GetString()!)])).ToList();
        var unserved = events.SelectMany(e => e.Item3.Append(e.Item2)).Distinct().Where(m => !services.Values.Any(s => s.Contains(m))).ToList();
        if (unserved.Count > 0)
        {
            throw new InvalidOperationException($"No service in {path} runs {string.Join(", ", unserved)}.");
        }

        return new BrokerPlan(services, events);
    }

    /// <summary>What OpenTofu reads: names in lower case, as the topics and queues are named.</summary>
    public string Render() => JsonSerializer.Serialize(new
    {
        comment = "Generated from events/subscriptions.json by BrokerPlanTests (REGENERATE_BROKER_PLAN=1). Do not edit.",
        topics = Topics.Select(t => t.ToLowerInvariant()),
        queues = services.Keys.Order(StringComparer.Ordinal),
        subscriptions = Subscriptions.Select(s => new { topic = s.Topic.ToLowerInvariant(), queue = s.Queue, filter_policy = s.FilterPolicy }),
    }, Indented) + "\n";
}
