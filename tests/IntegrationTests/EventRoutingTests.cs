using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Payments.Host;

namespace IntegrationTests;

/// <summary>s4-l01: every consumer of every event is routed, and no event is marked delivered for a consumer that never got it.</summary>
public sealed class EventRoutingTests(Database database)
{
    [Fact]
    public async Task The_monolith_routes_every_consumer_and_every_handler_is_in_the_subscriptions()
    {
        await using var host = database.StartHost();
        _ = host.CreateClient(); // starts the host: it would refuse to start if anything were unrouted

        var routes = host.Services.GetRequiredService<EventRoutes>();

        Assert.Empty(routes.Problems());
        Assert.All(host.Services.GetServices<EventHandlerRegistration>(),
            h => Assert.Contains(h.Module, host.Services.GetRequiredService<EventSubscriptions>().ConsumersOf(h.EventType.Name)));
    }

    [Fact]
    public async Task Payments_alone_refuses_to_start_and_names_the_consumers_it_cannot_reach()
    {
        await using var host = new WebApplicationFactory<PaymentsHostEntry>().WithWebHostBuilder(web => web
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Owner", database.Owner)
            .UseSetting("ConnectionStrings:Payments", database.Payments)
            .UseSetting("Payments:WebhookSecret", Database.WebhookSecret)
            .UseSetting("Payments:Settlement:PollEvery", "01:00:00")
            .UseSetting("Payments:Money:PollEvery", "01:00:00")
            .UseSetting("Auth:ClientId", Database.ClientId)
            .UseSetting("Auth:LocalSigningKey", Database.SigningKey));

        var refused = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        var message = refused.ToString();
        Assert.Contains("Pledges consumes PaymentMethodSaved", message, StringComparison.Ordinal);
        Assert.Contains("Notifications consumes PledgeChargeSucceeded", message, StringComparison.Ordinal);
        Assert.Contains("Campaigns consumes CampaignSettled", message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_consumer_routed_to_the_broker_without_a_publisher_is_a_problem()
    {
        var routes = Routes(new() { ["Remote"] = EventRoute.Broker }, published: [typeof(Spoken)]);

        Assert.Contains(routes.Problems(), p => p.Contains("Remote is routed Broker for Spoken", StringComparison.Ordinal));
    }

    [Fact]
    public void A_handler_missing_from_the_subscriptions_is_a_problem()
    {
        var routes = Routes([], published: [], handlers: [Registration<Unlisted>("Local")]);

        Assert.Contains(routes.Problems(), p => p.Contains("does not list Local as its consumer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_owes_each_local_consumer_and_the_broker_once()
    {
        var routes = Routes(new() { ["Remote"] = EventRoute.Broker }, published: [typeof(Spoken)]);

        Assert.Equal(["Local", OutboxSink.Broker], routes.SinksFor(nameof(Spoken), "Local"));
        Assert.Empty(routes.SinksFor("NobodyListens", "Local"));
    }

    [Fact]
    public void A_route_for_one_producer_wins_over_the_consumers_route()
    {
        var routes = Routes([], published: [typeof(Spoken)], producers: new() { ["Local"] = new() { ["Local"] = EventRoute.Broker } },
            configuredRemote: EventRoute.InProcess);

        Assert.Equal(EventRoute.Broker, routes.RouteOf("Local", "Local"));
        Assert.Equal(EventRoute.InProcess, routes.RouteOf("Local", "SomeoneElse"));
    }

    [Fact]
    public async Task An_event_with_no_consumer_in_this_program_is_never_marked_delivered()
    {
        // Spoken is published here; its only local consumer has no handler for it, and Remote is elsewhere.
        var module = new RecordingModule("Local");
        var routes = Routes(new() { ["Remote"] = EventRoute.Broker }, published: [typeof(Spoken)], modules: [module]);
        using var dispatcher = new OutboxDispatcher(new EventBus(), [module], [], [new PublishedEvent(typeof(Spoken), "Local")], routes,
            Options.Create(new OutboxOptions()), NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.Deliver(new OutboxRecord("Local", Guid.NewGuid(), nameof(Spoken), SpokenJson), TestContext.Current.CancellationToken);

        Assert.Equal(0, module.MarkedDelivered);
    }

    [Fact]
    public async Task A_row_owed_to_a_module_that_does_not_run_here_is_left_for_a_program_that_runs_it()
    {
        var module = new RecordingModule("Local");
        var routes = Routes([], published: [typeof(Spoken)], modules: [module],
            handlers: [new EventHandlerRegistration(typeof(Spoken), "Local.Handler", "Local", (_, _, _) => Task.CompletedTask)]);
        using var dispatcher = new OutboxDispatcher(new EventBus(), [module], [], [new PublishedEvent(typeof(Spoken), "Local")], routes,
            Options.Create(new OutboxOptions()), NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.Deliver(new OutboxRecord("Local", Guid.NewGuid(), nameof(Spoken), SpokenJson, Sinks: ["Local", "Remote"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, module.MarkedDelivered);
        Assert.Equal(0, module.Handled);
    }

    [Fact]
    public async Task A_row_follows_the_sinks_it_was_written_with_not_todays_routes()
    {
        // Written while Local took Spoken in process; Local has been routed to the broker since.
        var module = new RecordingModule("Local");
        var handler = new EventHandlerRegistration(typeof(Spoken), "Local.Handler", "Local", (_, _, _) => Task.CompletedTask);
        var routes = Routes(new() { ["Local"] = EventRoute.Broker }, published: [typeof(Spoken)], modules: [module], handlers: [handler]);
        using var dispatcher = new OutboxDispatcher(new EventBus(), [module], [handler], [new PublishedEvent(typeof(Spoken), "Local")], routes,
            Options.Create(new OutboxOptions()), NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.Deliver(new OutboxRecord("Local", Guid.NewGuid(), nameof(Spoken), SpokenJson, Sinks: ["Local"]), TestContext.Current.CancellationToken);

        Assert.Equal(1, module.Handled);
        Assert.Equal(1, module.MarkedDelivered);
    }

    private static EventRoutes Routes(Dictionary<string, EventRoute> configured, Type[] published,
        EventHandlerRegistration[]? handlers = null, IModuleEvents[]? modules = null,
        Dictionary<string, Dictionary<string, EventRoute>>? producers = null, EventRoute? configuredRemote = null)
    {
        if (configuredRemote is { } remote)
        {
            configured["Remote"] = remote;
        }

        return new(new EventSubscriptions(new Dictionary<string, EventDescription> { [nameof(Spoken)] = new("Local", ["Local", "Remote"]) }),
            modules ?? [new RecordingModule("Local")],
            handlers ?? [],
            published.Select(t => new PublishedEvent(t, "Local")),
            [],
            Options.Create(new EventRoutingOptions { Routes = configured, Producers = producers ?? [] }));
    }

    private static EventHandlerRegistration Registration<TEvent>(string module) =>
        new(typeof(TEvent), $"{module}.Handler", module, (_, _, _) => Task.CompletedTask);

    private sealed record Spoken(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    /// <summary>A Spoken as the outbox stores it: the dispatcher reads payloads strictly (ContractJson, s4-l06).</summary>
    private static readonly string SpokenJson = System.Text.Json.JsonSerializer.Serialize(
        new Spoken(Guid.NewGuid(), DateTimeOffset.UtcNow), BuildingBlocks.Application.ContractJson.Options);

    private sealed record Unlisted(Guid EventId, DateTimeOffset OccurredAt) : IntegrationEvent(EventId, OccurredAt);

    private sealed class RecordingModule(string module) : IModuleEvents
    {
        public int MarkedDelivered { get; private set; }

        public string Module => module;

        public Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, IReadOnlyCollection<string> types, int limit, TimeSpan claim, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);

        public Task<IReadOnlyList<OutboxRecord>> ClaimUnpublished(TimeSpan olderThan, int limit, TimeSpan claim, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task MarkPublished(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TimeSpan?> OldestUnpublishedAge(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);

        public Task<IReadOnlyList<OutboxRecord>> Published(string? eventType, DateTimeOffset from, DateTimeOffset to, OutboxRecord? after, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task MarkDelivered(Guid eventId, CancellationToken cancellationToken)
        {
            MarkedDelivered++;
            return Task.CompletedTask;
        }

        public Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public int Handled { get; private set; }

        public Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken cancellationToken)
        {
            Handled++;
            return Task.CompletedTask;
        }
    }
}
