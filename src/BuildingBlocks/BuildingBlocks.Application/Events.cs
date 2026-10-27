using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Application;

/// <summary>
/// Something that has happened in one module, told to the others. Every event carries the same
/// envelope: an id (what every inbox deduplicates on), the time it happened and a version.
/// </summary>
public abstract record IntegrationEvent(Guid EventId, DateTimeOffset OccurredAt)
{
    /// <summary>Raised when an event's fields change in a way old listeners cannot read.</summary>
    public virtual int Version => 1;
}

/// <summary>
/// Where a module records the events of a change. The row is written in the change's own
/// transaction, so the change and its events commit together or not at all.
/// </summary>
public interface IOutbox
{
    Task Add(IntegrationEvent integrationEvent, CancellationToken cancellationToken);

    /// <summary>Many events of one change, written in one round trip: a batch of thousands, for example.</summary>
    Task AddRange(IReadOnlyCollection<IntegrationEvent> integrationEvents, CancellationToken cancellationToken);
}

/// <summary>
/// Reacts to another module's event. It may run more than once for the same event, and events
/// may arrive out of order: the inbox drops a repeat, and the handler's own changes are conditional.
/// </summary>
#pragma warning disable CA1711 // "Handler" is the course's word for it; the type is not a .NET event delegate.
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task Handle(TEvent integrationEvent, CancellationToken cancellationToken);
}
#pragma warning restore CA1711

/// <summary>Each module's outbox is a keyed service; a handler asks for its own module's.</summary>
public static class Outboxes
{
    public static IOutbox GetOutbox(this IServiceProvider services, string module) =>
        services.GetRequiredKeyedService<IOutbox>(module);
}
