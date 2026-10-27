using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Events;

/// <summary>An event a module has committed, waiting to be handed to the other modules.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public required string Type { get; set; }

    public int Version { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public required string Payload { get; set; }

    /// <summary>Set when every in-process sink has its inbox row.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>Set when the broker accepted the row; owed only when <see cref="Sinks"/> holds <see cref="OutboxSink.Broker"/>.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// What the row owes, fixed from the routes in force when it was written: the modules that get it in
    /// process, and <see cref="OutboxSink.Broker"/> when it must be published. Null on rows written before
    /// routes existed (s4-l01): every consumer in the program, in process.
    /// </summary>
    public string[]? Sinks { get; set; }

    /// <summary>Until when a task's recovery pass holds the row for publishing; another task skips it.</summary>
    public DateTimeOffset? PublishClaimedUntil { get; set; }

    /// <summary>Until when a task's sweep holds the row for redelivery in process; another task skips it.</summary>
    public DateTimeOffset? DeliveryClaimedUntil { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>The trace of the change that wrote the event, so its handlers continue it.</summary>
    public string? TraceParent { get; set; }
}

/// <summary>An event one handler of this module has already handled.</summary>
public sealed class InboxMessage
{
    public required string Handler { get; set; }

    public Guid EventId { get; set; }

    public DateTimeOffset HandledAt { get; set; }
}

/// <summary>The sink names stored in an outbox row besides module names.</summary>
public static class OutboxSink
{
    /// <summary>The row must be published to the broker.</summary>
    public const string Broker = "broker";
}

public static class OutboxModel
{
    /// <summary>
    /// Rows that still owe an in-process delivery: the predicate of the partial index <c>outbox_undelivered</c>,
    /// and word for word the condition of every query that should use it (PostgreSQL uses a partial index
    /// only when the query's condition implies the index's).
    /// </summary>
    public const string Undelivered = "delivered_at IS NULL AND (sinks IS NULL OR cardinality(array_remove(sinks, 'broker')) > 0)";

    /// <summary>Rows that still owe a publish: the predicate of the partial index <c>outbox_unpublished</c>, likewise.</summary>
    public const string Unpublished = "published_at IS NULL AND 'broker' = ANY (sinks)";

    /// <summary>Maps the module's <c>outbox</c> and <c>inbox</c> tables; call it from the module's DbContext.</summary>
    public static ModelBuilder MapOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Type).HasMaxLength(200);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            outbox.Property(m => m.OccurredAt);
            outbox.Property(m => m.TraceParent).HasMaxLength(60);
            // Two partial indexes on the same column: named, or EF would take them for one.
            outbox.HasIndex(m => m.OccurredAt, "outbox_undelivered")
                .HasFilter(Undelivered)
                .HasDatabaseName("outbox_undelivered");
            outbox.HasIndex(m => m.OccurredAt, "outbox_unpublished")
                .HasFilter(Unpublished)
                .HasDatabaseName("outbox_unpublished");
        });
        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable("inbox");
            inbox.HasKey(m => new { m.Handler, m.EventId });
            inbox.Property(m => m.Handler).HasMaxLength(200);
            inbox.Property(m => m.HandledAt).HasDefaultValueSql("now()");
        });
        return modelBuilder;
    }
}
