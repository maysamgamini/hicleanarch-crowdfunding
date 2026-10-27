using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>How an event reaches one consuming module.</summary>
public enum EventRoute
{
    /// <summary>The module runs in this program: its handlers get the event from the in-process dispatcher.</summary>
    InProcess,

    /// <summary>The event is published to the broker (SNS and SQS), and the module's host reads it from its queue.</summary>
    Broker,
}

public sealed class EventRoutingOptions
{
    /// <summary>The file that names every event's consuming modules, relative to the program's folder.</summary>
    public string SubscriptionsFile { get; set; } = Path.Combine("events", "subscriptions.json");

    /// <summary>
    /// A route per consuming module. A module that runs in this program is <see cref="EventRoute.InProcess"/>
    /// unless set here; a module that does not run here has no route unless set here.
    /// </summary>
    public Dictionary<string, EventRoute> Routes { get; set; } = [];
}

/// <summary>Publishes outbox rows to the broker. Registered from s4-l02; a Broker route needs one.</summary>
public interface IEventPublisher
{
    Task Publish(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken);
}

/// <summary>One consumer of one event type, added by a program itself (tests only).</summary>
public sealed record EventSubscription(string EventType, string Module);

/// <summary>Every event type and the modules that consume it: the one list every host and Terraform read.</summary>
public sealed class EventSubscriptions(IReadOnlyDictionary<string, IReadOnlyList<string>> consumers)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Consumers => consumers;

    public IReadOnlyList<string> ConsumersOf(string eventType) => consumers.TryGetValue(eventType, out var c) ? c : [];

    /// <summary>This list with <paramref name="extra"/> consumers added.</summary>
    public EventSubscriptions With(IEnumerable<EventSubscription> extra)
    {
        var merged = consumers.ToDictionary(c => c.Key, c => c.Value.ToList());
        foreach (var s in extra)
        {
            if (!merged.TryGetValue(s.EventType, out var list))
            {
                merged[s.EventType] = list = [];
            }

            if (!list.Contains(s.Module))
            {
                list.Add(s.Module);
            }
        }

        return new EventSubscriptions(merged.ToDictionary(m => m.Key, m => (IReadOnlyList<string>)m.Value));
    }

    public static EventSubscriptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The event subscriptions file is missing: {path}.");
        }

        using var file = File.OpenRead(path);
        var document = JsonDocument.Parse(file);
        var consumers = document.RootElement.GetProperty("consumers").EnumerateObject()
            .ToDictionary(e => e.Name, e => (IReadOnlyList<string>)[.. e.Value.EnumerateArray().Select(m => m.GetString()!)]);
        return new EventSubscriptions(consumers);
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
    private readonly Dictionary<string, EventRoute> _configured;

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
        _configured = options.Value.Routes;
    }

    /// <summary>The consuming module's route, or null when this program cannot reach it.</summary>
    public EventRoute? RouteOf(string module) =>
        _configured.TryGetValue(module, out var route) ? route : _local.Contains(module) ? EventRoute.InProcess : null;

    /// <summary>
    /// What a new row of <paramref name="eventType"/> owes: each consumer routed in process, and
    /// <see cref="OutboxSink.Broker"/> once if any consumer is routed to the broker.
    /// </summary>
    public IReadOnlyList<string> SinksFor(string eventType)
    {
        var consumers = _subscriptions.ConsumersOf(eventType);
        var sinks = consumers.Where(c => RouteOf(c) == EventRoute.InProcess).ToList();
        if (consumers.Any(c => RouteOf(c) == EventRoute.Broker))
        {
            sinks.Add(OutboxSink.Broker);
        }

        return sinks;
    }

    /// <summary>The modules that run in this program.</summary>
    public bool RunsHere(string module) => _local.Contains(module);

    /// <summary>Handlers in this program that get <paramref name="eventType"/> in process.</summary>
    public IEnumerable<EventHandlerRegistration> InProcessHandlers(Type eventType) =>
        _handlers.Where(h => h.EventType == eventType && RouteOf(h.Module) == EventRoute.InProcess);

    /// <summary>Everything that would lose or misroute an event; the program must not start while any is listed.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var handler in _handlers.Where(h => !_subscriptions.ConsumersOf(h.EventType.Name).Contains(h.Module)))
        {
            problems.Add($"{handler.Handler} handles {handler.EventType.Name}, but events/subscriptions.json does not list {handler.Module} as its consumer.");
        }

        foreach (var eventType in _published.Select(p => p.EventType.Name).Distinct())
        {
            foreach (var consumer in _subscriptions.ConsumersOf(eventType))
            {
                switch (RouteOf(consumer))
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
