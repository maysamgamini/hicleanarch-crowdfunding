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

    public DbSet<Pledge> Pledges => Set<Pledge>();

    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    public DbSet<LedgerCause> LedgerCauses => Set<LedgerCause>();

    public DbSet<PledgeActivation> PledgeActivations => Set<PledgeActivation>();

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
            window.Property(w => w.OpenedAt).HasDefaultValueSql("now()");
            // The close job finds due windows on this index (lesson s2-l12).
            window.HasIndex(w => w.Deadline, "funding_windows_due").HasFilter("status IN ('LIVE', 'ENDED')").HasDatabaseName("funding_windows_due");
            // Written only by the close's SQL: when the decision was made, and the check a minute later (lesson s3-l05).
            window.Property<DateTimeOffset?>("DecidedAt");
            window.Property<DateTimeOffset?>("RecheckedAt");
            window.Property<long?>("RecheckMinor");
            window.HasIndex("DecidedAt").HasFilter("rechecked_at IS NULL").HasDatabaseName("funding_windows_to_recheck");
            // ... and funded windows whose marking is unfinished on this one (lesson s3-l08).
            window.HasIndex(w => w.Deadline, "funding_windows_marking").HasFilter("status = 'FUNDED' AND marking_finished_at IS NULL").HasDatabaseName("funding_windows_marking");
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

        modelBuilder.Entity<Pledge>(pledge =>
        {
            pledge.ToTable("pledges", t => t.HasCheckConstraint("pledges_amount_positive", "amount_minor > 0"));
            pledge.HasKey(p => p.Id);
            pledge.Property(p => p.Id).ValueGeneratedNever();
            pledge.Ignore(p => p.PublicId);
            pledge.Property(p => p.Status)
                .HasConversion(s => Application.StatusNames.Of(s), s => Application.StatusNames.Parse<PledgeStatus>(s))
                .HasMaxLength(20);
            pledge.Property(p => p.PaymentMethodRef).HasMaxLength(200);
            pledge.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            pledge.HasIndex(p => p.CampaignId);
            // A funded campaign's marking walks its active pledges in id order from the cursor.
            pledge.HasIndex(p => new { p.CampaignId, p.Id }).HasFilter("status = 'ACTIVE'").HasDatabaseName("pledges_active_by_campaign");
            // The sweeper finds holds that ran out on this index.
            pledge.HasIndex(p => p.HoldExpiresAt).HasFilter("status = 'PENDING_SETUP'").HasDatabaseName("pledges_hold_due");
            // Lesson 4: one live pledge per backer per campaign, whatever code path sends a second.
            pledge.HasIndex(p => new { p.CampaignId, p.BackerId })
                .IsUnique()
                .HasFilter("status IN ('PENDING_SETUP', 'ACTIVE')")
                .HasDatabaseName("one_live_pledge_per_backer");
        });

        modelBuilder.Entity<LedgerEntry>(entry =>
        {
            entry.ToTable("pledge_ledger", t =>
            {
                t.HasCheckConstraint("pledge_ledger_kind", "kind IN ('ACTIVATE', 'CHANGE', 'CANCEL', 'DROP')");
                t.HasCheckConstraint("pledge_ledger_sign",
                    "(kind = 'ACTIVATE' AND delta_minor > 0) OR (kind IN ('CANCEL', 'DROP') AND delta_minor < 0) OR (kind = 'CHANGE' AND delta_minor <> 0)");
            });
            // Partitioned by month on created_at (lesson s3-l10): every key must include it.
            entry.HasKey(e => new { e.EntryId, e.CreatedAt });
            entry.Property(e => e.EntryId).UseIdentityAlwaysColumn();
            entry.Property(e => e.CauseId).HasMaxLength(200);
            entry.Property(e => e.Kind)
                .HasConversion(k => Application.StatusNames.Of(k), k => Application.StatusNames.Parse<LedgerKind>(k))
                .HasMaxLength(10);
            entry.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entry.HasIndex(e => new { e.CampaignId, e.CreatedAt }).IncludeProperties(e => e.DeltaMinor);
        });

        // The replay guards that a partitioned table cannot hold: unique across all months.
        modelBuilder.Entity<LedgerCause>(cause =>
        {
            cause.ToTable("ledger_causes");
            cause.HasKey(c => new { c.CauseId, c.Kind });
            cause.Property(c => c.CauseId).HasMaxLength(200);
            cause.Property(c => c.Kind).HasMaxLength(10);
            cause.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            cause.HasIndex(c => c.CreatedAt);
        });

        modelBuilder.Entity<PledgeActivation>(activation =>
        {
            activation.ToTable("pledge_activations");
            activation.HasKey(a => a.PledgeId);
            activation.Property(a => a.PledgeId).ValueGeneratedNever();
        });
    }
}
