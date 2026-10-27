using BuildingBlocks.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>
/// The events one module's context added in this scope, handed to the bus once that context's
/// transaction commits. One per context, so one module's commit or rollback never touches another's.
/// </summary>
public sealed class PendingEvents<TContext>
    where TContext : DbContext
{
    public List<OutboxRecord> Records { get; } = [];
}

internal sealed class EfOutbox<TContext>(TContext db, PendingEvents<TContext> pending, EventRoutes routes, string module) : IOutbox
    where TContext : DbContext
{
    public Task Add(IntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        AddRange([integrationEvent], cancellationToken);

    public async Task AddRange(IReadOnlyCollection<IntegrationEvent> integrationEvents, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An event is written in the transaction of the change it reports; start one first.");
        }

        var messages = integrationEvents.Select(EventBus.ToMessage).ToList();
        foreach (var message in messages)
        {
            // What the row owes is decided now, from this program's routes, and stored with it: a later
            // change of routes never makes an old row owe something new, or a new one owe nothing.
            message.Sinks = [.. routes.SinksFor(message.Type, module)];
        }

        db.AddRange(messages);
        await db.SaveChangesAsync(cancellationToken); // one SaveChanges: EF Core sends the rows in batched commands
        pending.Records.AddRange(messages.Select(m => new OutboxRecord(module, m.Id, m.Type, m.Payload, m.TraceParent, m.Sinks, m.Version, m.OccurredAt)));
    }
}

/// <summary>Right after the commit, the events of the transaction go to the bus; after a rollback, nowhere.</summary>
public sealed class OutboxCommitInterceptor<TContext>(PendingEvents<TContext> pending, EventBus bus, PublishHandoff? publishing = null)
    : DbTransactionInterceptor
    where TContext : DbContext
{
    public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush();
        return Task.CompletedTask;
    }

    public override void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => Flush();

    public override Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        pending.Records.Clear();
        return Task.CompletedTask;
    }

    public override void TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => pending.Records.Clear();

    private void Flush()
    {
        foreach (var record in pending.Records)
        {
            if (record.Sinks is null || record.Sinks.Any(s => s != OutboxSink.Broker))
            {
                bus.Publish(record);
            }

            if (publishing is not null && record.Sinks?.Contains(OutboxSink.Broker) == true)
            {
                publishing.Offer(record); // a full queue never blocks the commit: the row waits for recovery
            }
        }

        pending.Records.Clear();
    }
}
