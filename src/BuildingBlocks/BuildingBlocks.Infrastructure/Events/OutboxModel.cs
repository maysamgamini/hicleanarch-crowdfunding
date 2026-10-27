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

    public DateTimeOffset? DeliveredAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>An event one handler of this module has already handled.</summary>
public sealed class InboxMessage
{
    public required string Handler { get; set; }

    public Guid EventId { get; set; }

    public DateTimeOffset HandledAt { get; set; }
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
            outbox.HasIndex(m => m.OccurredAt).HasFilter("delivered_at IS NULL").HasDatabaseName("outbox_undelivered");
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
