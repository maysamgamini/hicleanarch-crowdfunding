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

internal sealed class EfOutbox<TContext>(TContext db, PendingEvents<TContext> pending, string module) : IOutbox
    where TContext : DbContext
{
    public async Task Add(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("An event is written in the transaction of the change it reports; start one first.");
        }

        var message = EventBus.ToMessage(integrationEvent);
        db.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        pending.Records.Add(new OutboxRecord(module, message.Id, message.Type, message.Payload));
    }
}

/// <summary>Right after the commit, the events of the transaction go to the bus; after a rollback, nowhere.</summary>
public sealed class OutboxCommitInterceptor<TContext>(PendingEvents<TContext> pending, EventBus bus) : DbTransactionInterceptor
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
            bus.Publish(record);
        }

        pending.Records.Clear();
    }
}
