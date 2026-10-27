using BuildingBlocks.Infrastructure.Events;

using Microsoft.EntityFrameworkCore;

using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>The Payments module's view of the database: its own schema, and nothing else.</summary>
internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string Schema = "payments";

    public DbSet<PspCustomer> Customers => Set<PspCustomer>();

    public DbSet<CardSetup> CardSetups => Set<CardSetup>();

    public DbSet<WebhookMessage> WebhookInbox => Set<WebhookMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<PspCustomer>(customer =>
        {
            customer.ToTable("psp_customers");
            customer.HasKey(c => c.BackerId);
            customer.Property(c => c.BackerId).ValueGeneratedNever();
            customer.Property(c => c.PspCustomerId).HasMaxLength(100);
            customer.HasIndex(c => c.PspCustomerId).IsUnique();
        });

        modelBuilder.Entity<CardSetup>(setup =>
        {
            setup.ToTable("card_setups");
            setup.HasKey(s => s.Id);
            setup.Property(s => s.Id).ValueGeneratedNever();
            setup.Property(s => s.PspSetupId).HasMaxLength(100);
            setup.HasIndex(s => s.PspSetupId).IsUnique();
            setup.HasIndex(s => s.PledgeId);
            setup.Property(s => s.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<CardSetupStatus>(s, true))
                .HasMaxLength(20);
            setup.Property(s => s.PaymentMethodRef).HasMaxLength(200);
            setup.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WebhookMessage>(message =>
        {
            message.ToTable("webhook_inbox");
            message.HasKey(m => m.MessageId);
            message.Property(m => m.MessageId).HasMaxLength(100);
            message.Property(m => m.ReceivedAt).HasDefaultValueSql("now()");
        });
    }
}

/// <summary>A PSP message we have seen, by the PSP's own id.</summary>
internal sealed class WebhookMessage
{
    public required string MessageId { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}
