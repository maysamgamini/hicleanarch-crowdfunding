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

        modelBuilder.Entity<CampaignPledge>(pledge =>
        {
            pledge.ToTable("campaign_pledges");
            pledge.HasKey(p => p.PledgeId);
            pledge.Property(p => p.PledgeId).ValueGeneratedNever();
            // A campaign's backers are read on this index: its pledges that counted and have not left.
            pledge.HasIndex(p => p.CampaignId).HasFilter("activated_at IS NOT NULL AND cancelled_at IS NULL AND released_at IS NULL")
                .HasDatabaseName("campaign_pledges_live");
        });

        modelBuilder.Entity<CampaignCreator>(creator =>
        {
            creator.ToTable("campaign_creators");
            creator.HasKey(c => c.CampaignId);
            creator.Property(c => c.CampaignId).ValueGeneratedNever();
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

/// <summary>
/// One pledge, as Notifications has heard of it from Pledges' events: its own copy. Each event only
/// sets its own timestamp, once, so a late or repeated event never undoes a newer one: a cancel heard
/// before its activation leaves a row that the activation cannot bring back. A campaign's backers are
/// the backers of its live rows.
/// </summary>
internal sealed class CampaignPledge
{
    public Guid PledgeId { get; init; }

    public Guid CampaignId { get; init; }

    public Guid BackerId { get; init; }

    /// <summary>When the pledge counted, as <c>PledgeActivated</c> says.</summary>
    public DateTimeOffset? ActivatedAt { get; init; }

    /// <summary>When the backer cancelled it, as <c>PledgeCancelled</c> says.</summary>
    public DateTimeOffset? CancelledAt { get; init; }

    /// <summary>When it was released because the campaign was cancelled, as <c>PledgeReleased</c> says.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }
}

/// <summary>Who created a campaign, as Notifications heard it when the campaign was published.</summary>
internal sealed class CampaignCreator
{
    public Guid CampaignId { get; init; }

    public Guid CreatorId { get; init; }
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
