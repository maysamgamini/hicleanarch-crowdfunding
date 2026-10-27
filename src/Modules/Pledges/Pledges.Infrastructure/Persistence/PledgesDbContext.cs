using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

using Pledges.Domain;

namespace Pledges.Infrastructure.Persistence;

/// <summary>The Pledges module's view of the database: its own schema, and nothing else.</summary>
internal sealed class PledgesDbContext(DbContextOptions<PledgesDbContext> options) : DbContext(options)
{
    public const string Schema = "pledges";

    public DbSet<FundingWindow> FundingWindows => Set<FundingWindow>();

    public DbSet<TierCounter> TierCounters => Set<TierCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapIdempotencyKeys();
        modelBuilder.MapOutboxAndInbox();

        modelBuilder.Entity<FundingWindow>(window =>
        {
            window.ToTable("funding_windows");
            window.HasKey(w => w.CampaignId);
            window.Property(w => w.CampaignId).ValueGeneratedNever();
            window.Property(w => w.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<FundingStatus>(s, true))
                .HasMaxLength(20);
            window.Property(w => w.Currency).HasMaxLength(3).IsFixedLength();
            // The close job finds due windows on this index (lesson s2-l12).
            window.HasIndex(w => w.Deadline).HasFilter("status IN ('LIVE', 'ENDED')").HasDatabaseName("funding_windows_due");
        });

        modelBuilder.Entity<TierCounter>(tier =>
        {
            tier.ToTable("tier_counters", t =>
            {
                t.HasCheckConstraint("tier_counters_limit_positive", "limit_qty IS NULL OR limit_qty > 0");
                t.HasCheckConstraint("tier_counters_claimed_not_negative", "claimed >= 0");
                t.HasCheckConstraint("tier_counters_held_not_negative", "held >= 0");
                t.HasCheckConstraint("never_oversell", "limit_qty IS NULL OR claimed + held <= limit_qty");
            });
            tier.HasKey(t => t.TierId);
            tier.Property(t => t.TierId).ValueGeneratedNever();
            tier.HasIndex(t => t.CampaignId);
        });
    }
}
