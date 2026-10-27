using System.Text.Json;
using System.Text.Json.Nodes;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Events;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Payments.Contracts;

namespace Contracts.ConsumerTests;

/// <summary>
/// The in-process dispatcher reads events with the same strict options as the consumer tests: a
/// published example is handled, an unknown field changes nothing, and an event without a required
/// field reaches no handler. Its row stays undelivered, with the error logged, instead of being handled
/// with a zero.
/// </summary>
public class DispatcherReadingTests
{
    [Fact]
    public async Task A_published_example_with_an_unknown_field_reaches_the_handler()
    {
        var example = Published.Example("Payments", "PledgeChargeSucceeded.v1.json");
        example["currency"] = "USD";
        var (dispatcher, handled) = Dispatcher();

        await dispatcher.Deliver(Record(example), TestContext.Current.CancellationToken);

        var charged = Assert.Single(handled);
        Assert.Equal(6000, charged.AmountMinor);
    }

    [Fact]
    public async Task An_event_missing_a_required_field_reaches_no_handler()
    {
        var example = Published.Example("Payments", "PledgeChargeSucceeded.v1.json");
        example.Remove("amountMinor");
        var (dispatcher, handled) = Dispatcher();

        var refused = await Assert.ThrowsAsync<JsonException>(() => dispatcher.Deliver(Record(example), TestContext.Current.CancellationToken));

        Assert.Contains("amountMinor", refused.Message, StringComparison.Ordinal);
        Assert.Empty(handled);
    }

    private static OutboxRecord Record(JsonObject payload) =>
        new("Payments", Guid.Parse((string)payload["eventId"]!), nameof(PledgeChargeSucceeded), payload.ToJsonString());

    private static (OutboxDispatcher Dispatcher, List<PledgeChargeSucceeded> Handled) Dispatcher()
    {
        var handled = new List<PledgeChargeSucceeded>();
        var registration = new EventHandlerRegistration(typeof(PledgeChargeSucceeded), "Pledges.FollowCharges", "Pledges", (_, e, _) =>
        {
            handled.Add((PledgeChargeSucceeded)e);
            return Task.CompletedTask;
        });
        IModuleEvents[] modules = [new InMemoryModule("Payments"), new InMemoryModule("Pledges")];
        var routes = new EventRoutes(
            new EventSubscriptions(new Dictionary<string, EventDescription> { [nameof(PledgeChargeSucceeded)] = new("Payments", ["Pledges"]) }),
            modules, [registration], [], [], Options.Create(new EventRoutingOptions()));
        var dispatcher = new OutboxDispatcher(new EventBus(), modules, [registration], [], routes,
            Options.Create(new OutboxOptions()), NullLogger<OutboxDispatcher>.Instance);
        return (dispatcher, handled);
    }

    /// <summary>A module whose inbox runs the handler at once; no database.</summary>
    private sealed class InMemoryModule(string module) : IModuleEvents
    {
        public string Module => module;

        public Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, IReadOnlyCollection<string> types, int limit, TimeSpan claim,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);

        public Task<IReadOnlyList<OutboxRecord>> ClaimUnpublished(TimeSpan olderThan, int limit, TimeSpan claim, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task MarkPublished(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TimeSpan?> OldestUnpublishedAge(CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);

        public Task<IReadOnlyList<OutboxRecord>> Published(string? eventType, DateTimeOffset from, DateTimeOffset to, OutboxRecord? after, int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxRecord>>([]);

        public Task MarkDelivered(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken cancellationToken) =>
            handle(new NoServices(), cancellationToken);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
