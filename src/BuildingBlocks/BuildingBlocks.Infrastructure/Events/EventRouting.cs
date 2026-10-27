using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>How an event reaches one consuming module.</summary>
public enum EventRoute
{
    /// <summary>The module runs in this program: its handlers get the event from the in-process dispatcher.</summary>
    InProcess,

    /// <summary>The event is published to its producer's topic, and the module's host reads it from its queue.</summary>
    Broker,
}

public sealed class EventRoutingOptions
{
    /// <summary>The file that names every event's producer and consumers, relative to the program's folder.</summary>
    public string SubscriptionsFile { get; set; } = Path.Combine("events", "subscriptions.json");

    /// <summary>
    /// A route per consuming module, for every producer. A module that runs in this program is
    /// <see cref="EventRoute.InProcess"/> unless set here; a module that does not run here has no route unless set here.
    /// </summary>
    public Dictionary<string, EventRoute> Routes { get; set; } = [];

    /// <summary>
    /// Routes for the events of one producing module, by consumer: they win over <see cref="Routes"/>.
    /// <c>Events:Producers:Payments:Pledges = Broker</c> sends Payments' events to Pledges through the broker.
    /// </summary>
    public Dictionary<string, Dictionary<string, EventRoute>> Producers { get; set; } = [];
}

/// <summary>
/// Publishes outbox rows to the broker, and returns the ids of the rows the broker accepted. A row it
/// did not accept stays unpublished, for the recovery pass.
/// </summary>
public interface IEventPublisher
{
    Task<IReadOnlyCollection<Guid>> Publish(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken);
}

/// <summary>One consumer of one event type, added by a program itself (tests only).</summary>
public sealed record EventSubscription(string EventType, string Module, string Producer);

/// <summary>An event type, the module that produces it and the modules that consume it.</summary>
public sealed record EventDescription(string Producer, IReadOnlyList<string> Consumers);

/// <summary>Every event type, its producer and its consumers: the one list every host and Terraform read.</summary>
public sealed class EventSubscriptions(IReadOnlyDictionary<string, EventDescription> events)
{
    public IReadOnlyDictionary<string, EventDescription> Events => events;

    public IReadOnlyList<string> ConsumersOf(string eventType) => events.TryGetValue(eventType, out var e) ? e.Consumers : [];

    public string? ProducerOf(string eventType) => events.TryGetValue(eventType, out var e) ? e.Producer : null;

    public static EventSubscriptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The event subscriptions file is missing: {path}.");
        }

        using var file = File.OpenRead(path);
        using var document = JsonDocument.Parse(file);
        var events = document.RootElement.GetProperty("events").EnumerateObject().ToDictionary(
            e => e.Name,
            e => new EventDescription(
                e.Value.GetProperty("producer").GetString()!,
                [.. e.Value.GetProperty("consumers").EnumerateArray().Select(m => m.GetString()!)]));
        return new EventSubscriptions(events);
    }

    /// <summary>This list with <paramref name="extra"/> consumers added.</summary>
    public EventSubscriptions With(IEnumerable<EventSubscription> extra)
    {
        var merged = events.ToDictionary(e => e.Key, e => (e.Value.Producer, Consumers: e.Value.Consumers.ToList()));
        foreach (var s in extra)
        {
            if (!merged.TryGetValue(s.EventType, out var entry))
            {
                merged[s.EventType] = entry = (s.Producer, []);
            }

            if (!entry.Consumers.Contains(s.Module))
            {
                entry.Consumers.Add(s.Module);
            }
        }

        return new EventSubscriptions(merged.ToDictionary(m => m.Key, m => new EventDescription(m.Value.Producer, m.Value.Consumers)));
    }
}

/// <summary>
/// Where this program sends each event: to the handlers of a consuming module that runs here, or
/// to the broker for a module that runs elsewhere. Decided when a row is written, and stored with it.
/// </summary>
public sealed class EventRoutes
{
    private readonly EventSubscriptions _subscriptions;
    private readonly HashSet<string> _local;
    private readonly IReadOnlyList<EventHandlerRegistration> _handlers;
    private readonly IReadOnlyList<PublishedEvent> _published;
    private readonly bool _hasBroker;
    private readonly EventRoutingOptions _options;

    public EventRoutes(
        EventSubscriptions subscriptions,
        IEnumerable<IModuleEvents> modules,
        IEnumerable<EventHandlerRegistration> handlers,
        IEnumerable<PublishedEvent> published,
        IEnumerable<IEventPublisher> publishers,
        IOptions<EventRoutingOptions> options)
    {
        _subscriptions = subscriptions;
        _local = [.. modules.Select(m => m.Module)];
        _handlers = [.. handlers];
        _published = [.. published];
        _hasBroker = publishers.Any();
        _options = options.Value;
    }

    public EventSubscriptions Subscriptions => _subscriptions;

    /// <summary>How the events of <paramref name="producer"/> reach <paramref name="consumer"/>, or null when this program cannot reach it.</summary>
    public EventRoute? RouteOf(string consumer, string producer) =>
        _options.Producers.TryGetValue(producer, out var byConsumer) && byConsumer.TryGetValue(consumer, out var forProducer) ? forProducer
        : _options.Routes.TryGetValue(consumer, out var route) ? route
        : _local.Contains(consumer) ? EventRoute.InProcess
        : null;

    /// <summary>
    /// What a new row of <paramref name="eventType"/> written by <paramref name="producer"/> owes: each consumer
    /// routed in process, and <see cref="OutboxSink.Broker"/> once if any consumer is routed to the broker.
    /// </summary>
    public IReadOnlyList<string> SinksFor(string eventType, string producer)
    {
        var consumers = _subscriptions.ConsumersOf(eventType);
        var sinks = consumers.Where(c => RouteOf(c, producer) == EventRoute.InProcess).ToList();
        if (consumers.Any(c => RouteOf(c, producer) == EventRoute.Broker))
        {
            sinks.Add(OutboxSink.Broker);
        }

        return sinks;
    }

    /// <summary>Whether <paramref name="module"/> runs in this program.</summary>
    public bool RunsHere(string module) => _local.Contains(module);

    /// <summary>Handlers in this program for <paramref name="eventType"/> whose module gets it from <paramref name="producer"/> by <paramref name="route"/>.</summary>
    public IEnumerable<EventHandlerRegistration> HandlersFor(Type eventType, string producer, EventRoute route) =>
        _handlers.Where(h => h.EventType == eventType && RouteOf(h.Module, producer) == route);

    /// <summary>Every event type a handler in this program takes, by name.</summary>
    public IReadOnlyDictionary<string, Type> HandledTypes() =>
        _handlers.Select(h => h.EventType).Distinct().ToDictionary(t => t.Name);

    /// <summary>Everything that would lose or misroute an event; the program must not start while any is listed.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var handler in _handlers.Where(h => !_subscriptions.ConsumersOf(h.EventType.Name).Contains(h.Module)))
        {
            problems.Add($"{handler.Handler} handles {handler.EventType.Name}, but events/subscriptions.json does not list {handler.Module} as its consumer.");
        }

        foreach (var published in _published.DistinctBy(p => p.EventType))
        {
            var eventType = published.EventType.Name;
            if (_subscriptions.ProducerOf(eventType) is { } listed && listed != published.Module)
            {
                problems.Add($"{published.Module} publishes {eventType}, but events/subscriptions.json names {listed} as its producer.");
            }

            foreach (var consumer in _subscriptions.ConsumersOf(eventType))
            {
                switch (RouteOf(consumer, published.Module))
                {
                    case null:
                        problems.Add($"{consumer} consumes {eventType}, which this program publishes, but {consumer} does not run here and has no route.");
                        break;
                    case EventRoute.InProcess when !_local.Contains(consumer):
                        problems.Add($"{consumer} is routed InProcess for {eventType}, but {consumer} does not run here.");
                        break;
                    case EventRoute.InProcess when !_handlers.Any(h => h.Module == consumer && h.EventType.Name == eventType):
                        problems.Add($"{consumer} consumes {eventType}, but no handler of {consumer} for it is registered.");
                        break;
                    case EventRoute.Broker when !_hasBroker:
                        problems.Add($"{consumer} is routed Broker for {eventType}, but this program has no broker publisher.");
                        break;
                }
            }
        }

        return problems;
    }
}

/// <summary>Refuses to start a program whose events are not all routed, naming each consumer.</summary>
internal sealed class EventRoutesCheck(EventRoutes routes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var problems = routes.Problems();
        return problems.Count == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException("The events of this program are not all routed:" + string.Concat(problems.Select(p => "\n - " + p)));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
