using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>One module's outbox and inbox, as the dispatcher sees them.</summary>
public interface IModuleEvents
{
    string Module { get; }

    /// <summary>The oldest undelivered events, at most <paramref name="limit"/>; with <paramref name="occurredBefore"/>, only those older.</summary>
    Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, int limit, CancellationToken cancellationToken);

    Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken);

    Task MarkDelivered(Guid eventId, CancellationToken cancellationToken);

    Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one handler for one event in one transaction of this module: the inbox row first, and
    /// if it was already there, nothing else.
    /// </summary>
    Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken cancellationToken);
}

internal sealed class ModuleEvents<TContext>(string module, IServiceScopeFactory scopes) : IModuleEvents
    where TContext : DbContext
{
    public string Module => module;

    public async Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var before = occurredBefore ?? DateTimeOffset.MaxValue;
        return await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.DeliveredAt == null && (m.Sinks == null || m.Sinks.Length > (m.Sinks.Contains(OutboxSink.Broker) ? 1 : 0)) && m.OccurredAt < before)
            .OrderBy(m => m.OccurredAt)
            .Take(limit)
            .Select(m => new OutboxRecord(module, m.Id, m.Type, m.Payload, m.TraceParent, m.Sinks))
            .ToListAsync(cancellationToken);
    }

    public async Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var oldest = await db.Set<OutboxMessage>()
            .Where(m => m.DeliveredAt == null && (m.Sinks == null || m.Sinks.Length > (m.Sinks.Contains(OutboxSink.Broker) ? 1 : 0)))
            .MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);
        return oldest is null ? null : DateTimeOffset.UtcNow - oldest.Value;
    }

    public async Task MarkDelivered(Guid eventId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<OutboxMessage>().Where(m => m.Id == eventId && m.DeliveredAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.DeliveredAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<OutboxMessage>().Where(m => m.Id == eventId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.Attempts, m => m.Attempts + 1)
                .SetProperty(m => m.LastError, error), cancellationToken);
    }

    public async Task HandleOnce(string handler, Guid eventId, Func<IServiceProvider, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The schema is the module's own, from its model; the values are parameters.
        var sql = "INSERT INTO \"" + db.Model.GetDefaultSchema() + "\".inbox (handler, event_id) VALUES ({0}, {1}) ON CONFLICT DO NOTHING";
        var inserted = await db.Database.ExecuteSqlRawAsync(sql, [handler, eventId], cancellationToken);
        if (inserted == 0)
        {
            await transaction.CommitAsync(cancellationToken); // handled before: nothing to do
            return;
        }

        await handle(scope.ServiceProvider, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
