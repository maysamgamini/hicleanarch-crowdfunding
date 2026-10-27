using BuildingBlocks.Infrastructure.Persistence;

using Campaigns.Domain;

using Microsoft.EntityFrameworkCore;

namespace Campaigns.Infrastructure.Persistence;

/// <summary>The Campaigns module's view of the database: its own schema, and nothing else.</summary>
internal sealed class CampaignsDbContext(DbContextOptions<CampaignsDbContext> options) : DbContext(options)
{
    public const string Schema = "campaigns";

    public DbSet<Campaign> Campaigns => Set<Campaign>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapIdempotencyKeys();

        modelBuilder.Entity<Campaign>(campaign =>
        {
            campaign.ToTable("campaigns");
            campaign.HasKey(c => c.Id);
            campaign.Property(c => c.Id).HasConversion(id => id.Value, value => new CampaignId(value));
            campaign.Property(c => c.Title).HasMaxLength(120);
            campaign.Property(c => c.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<CampaignStatus>(s, true))
                .HasMaxLength(20);
            campaign.ComplexProperty(c => c.Goal, goal =>
            {
                goal.Property(m => m.AmountMinor).HasColumnName("goal_minor");
                goal.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
            });
            campaign.OwnsMany(c => c.Tiers, tier =>
            {
                tier.ToTable("tier_definitions");
                tier.WithOwner().HasForeignKey("CampaignId");
                tier.HasKey(t => t.Id);
                tier.Property(t => t.Id).HasConversion(id => id.Value, value => new TierId(value));
                tier.Property(t => t.Title).HasMaxLength(120);
            });
            campaign.Navigation(c => c.Tiers).HasField("_tiers").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
