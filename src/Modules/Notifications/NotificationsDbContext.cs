using BuildingBlocks.Infrastructure.Events;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Notifications;

/// <summary>Notifications' view of the database: its own schema, and nothing else.</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<SentNotification>(sent =>
        {
            sent.ToTable("sent_notifications");
            // One send per event and recipient: a repeated event finds its row and sends nothing.
            sent.HasKey(s => new { s.EventId, s.RecipientId });
            sent.Property(s => s.Kind).HasMaxLength(40);
            sent.Property(s => s.SentAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<NotificationPreference>(preference =>
        {
            preference.ToTable("notification_preferences");
            preference.HasKey(p => new { p.UserId, p.Kind });
            preference.Property(p => p.Kind).HasMaxLength(40);
        });

        modelBuilder.Entity<CampaignBacker>(backer =>
        {
            backer.ToTable("campaign_backers");
            backer.HasKey(b => new { b.CampaignId, b.BackerId });
        });
    }
}

internal sealed class SentNotification
{
    public Guid EventId { get; init; }

    public Guid RecipientId { get; init; }

    public required string Kind { get; init; }

    public DateTimeOffset SentAt { get; init; }
}

internal sealed class NotificationPreference
{
    public Guid UserId { get; init; }

    public required string Kind { get; init; }

    public bool Enabled { get; set; }
}

/// <summary>Who backs a campaign, as Notifications has heard it: its own copy, built from Pledges' events.</summary>
internal sealed class CampaignBacker
{
    public Guid CampaignId { get; init; }

    public Guid BackerId { get; init; }
}

/// <summary>Used only by <c>dotnet ef migrations add</c>; it never connects.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(NotificationsDbOptions.Configure(new DbContextOptionsBuilder<NotificationsDbContext>(), "Host=localhost").Options);
}

internal static class NotificationsDbOptions
{
    public static DbContextOptionsBuilder<NotificationsDbContext> Configure(DbContextOptionsBuilder<NotificationsDbContext> builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", NotificationsDbContext.Schema))
            .UseSnakeCaseNamingConvention();
}
