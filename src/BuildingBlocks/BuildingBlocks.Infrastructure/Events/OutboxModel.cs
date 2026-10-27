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
            outbox.HasIndex(m => m.OccurredAt)
                .HasFilter("delivered_at IS NULL AND (sinks IS NULL OR cardinality(array_remove(sinks, 'broker')) > 0)")
                .HasDatabaseName("outbox_undelivered");
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
