using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>One module's outbox and inbox, as the dispatcher sees them.</summary>
public interface IModuleEvents
{
    string Module { get; }

    /// <summary>
    /// Claims the oldest undelivered events of <paramref name="types"/> (those this program can deliver) for
    /// <paramref name="claim"/>, at most <paramref name="limit"/>; with <paramref name="occurredBefore"/>, only
    /// those older. Rows another task holds are skipped (<c>FOR UPDATE SKIP LOCKED</c> and a claim time), so
    /// two tasks never redeliver the same row at once.
    /// </summary>
    Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, IReadOnlyCollection<string> types, int limit, TimeSpan claim, CancellationToken cancellationToken);

    Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken);

    Task MarkDelivered(Guid eventId, CancellationToken cancellationToken);

    Task RecordFailure(Guid eventId, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="limit"/> unpublished rows older than <paramref name="olderThan"/> for
    /// <paramref name="claim"/>, skipping rows another task holds (<c>FOR UPDATE SKIP LOCKED</c> and a claim time).
    /// </summary>
    Task<IReadOnlyList<OutboxRecord>> ClaimUnpublished(TimeSpan olderThan, int limit, TimeSpan claim, CancellationToken cancellationToken);

    Task MarkPublished(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken);

    Task<TimeSpan?> OldestUnpublishedAge(CancellationToken cancellationToken);

    /// <summary>
    /// Rows already published in a time range, optionally of one type, in (occurred, id) order after
    /// <paramref name="after"/>: what a replay sends again, a page at a time.
    /// </summary>
    Task<IReadOnlyList<OutboxRecord>> Published(string? eventType, DateTimeOffset from, DateTimeOffset to, OutboxRecord? after, int limit, CancellationToken cancellationToken);

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

    public async Task<IReadOnlyList<OutboxRecord>> Undelivered(DateTimeOffset? occurredBefore, IReadOnlyCollection<string> types, int limit, TimeSpan claim, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // The schema is the module's own, from its model; the values are parameters.
        var schema = db.Model.GetDefaultSchema();
        var sql = $$"""
            UPDATE "{{schema}}".outbox SET delivery_claimed_until = now() + {2} * interval '1 second'
            WHERE id IN (
                SELECT id FROM "{{schema}}".outbox
                WHERE {{OutboxModel.Undelivered}}
                  AND occurred_at < {0} AND type = ANY ({3})
                  AND (delivery_claimed_until IS NULL OR delivery_claimed_until < now())
                ORDER BY occurred_at
                LIMIT {1}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """;
        var rows = await db.Set<OutboxMessage>().FromSqlRaw(sql, occurredBefore ?? DateTimeOffset.MaxValue, limit, claim.TotalSeconds, types.ToArray())
            .AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.OrderBy(m => m.OccurredAt).Select(m => ToRecord(module, m))];
    }

    public Task<TimeSpan?> OldestUndeliveredAge(CancellationToken cancellationToken) => OldestAge(OutboxModel.Undelivered, cancellationToken);

    /// <summary>The age of the oldest row matching <paramref name="predicate"/>, an index's own predicate, so the index answers it.</summary>
    private async Task<TimeSpan?> OldestAge(string predicate, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // The schema is the module's own, from its model; the predicate is a constant.
        var sql = $"SELECT min(occurred_at) AS \"Value\" FROM \"{db.Model.GetDefaultSchema()}\".outbox WHERE {predicate}";
        var oldest = await db.Database.SqlQueryRaw<DateTimeOffset?>(sql).SingleAsync(cancellationToken);
        return oldest is null ? null : DateTimeOffset.UtcNow - oldest.Value;
    }

    public async Task MarkDelivered(Guid eventId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<OutboxMessage>().Where(m => m.Id == eventId && m.DeliveredAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.DeliveredAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxRecord>> ClaimUnpublished(TimeSpan olderThan, int limit, TimeSpan claim, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // The schema is the module's own, from its model; the values are parameters.
        var schema = db.Model.GetDefaultSchema();
        var sql = $$"""
            UPDATE "{{schema}}".outbox SET publish_claimed_until = now() + {2} * interval '1 second'
            WHERE id IN (
                SELECT id FROM "{{schema}}".outbox
                WHERE {{OutboxModel.Unpublished}}
                  AND occurred_at < now() - {0} * interval '1 second'
                  AND (publish_claimed_until IS NULL OR publish_claimed_until < now())
                ORDER BY occurred_at
                LIMIT {1}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """;
        var rows = await db.Set<OutboxMessage>().FromSqlRaw(sql, olderThan.TotalSeconds, limit, claim.TotalSeconds)
            .AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.OrderBy(m => m.OccurredAt).Select(m => ToRecord(module, m))];
    }

    public async Task MarkPublished(IReadOnlyCollection<Guid> eventIds, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<OutboxMessage>().Where(m => eventIds.Contains(m.Id) && m.PublishedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public Task<TimeSpan?> OldestUnpublishedAge(CancellationToken cancellationToken) => OldestAge(OutboxModel.Unpublished, cancellationToken);

    public async Task<IReadOnlyList<OutboxRecord>> Published(string? eventType, DateTimeOffset from, DateTimeOffset to, OutboxRecord? after, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // Keyset paging on (occurred_at, id): one commit can write a million rows with the same time.
        var schema = db.Model.GetDefaultSchema();
        var sql = $$"""
            SELECT * FROM "{{schema}}".outbox
            WHERE published_at IS NOT NULL AND occurred_at >= {0} AND occurred_at < {1}
              AND ({2}::text IS NULL OR type = {2})
              AND (occurred_at, id) > ({3}, {4})
            ORDER BY occurred_at, id
            LIMIT {5}
            """;
        var rows = await db.Set<OutboxMessage>()
            .FromSqlRaw(sql, from, to, (object?)eventType ?? DBNull.Value, after?.OccurredAt ?? DateTimeOffset.MinValue, after?.EventId ?? Guid.Empty, limit)
            .AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.Select(m => ToRecord(module, m))];
    }

    private static OutboxRecord ToRecord(string module, OutboxMessage m) =>
        new(module, m.Id, m.Type, m.Payload, m.TraceParent, m.Sinks, m.Version, m.OccurredAt);

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
