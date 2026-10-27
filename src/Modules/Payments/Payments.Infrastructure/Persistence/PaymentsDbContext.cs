using BuildingBlocks.Infrastructure.Events;
using BuildingBlocks.Infrastructure.Persistence;

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

    public DbSet<Charge> Charges => Set<Charge>();

    public DbSet<ChargeAttempt> ChargeAttempts => Set<ChargeAttempt>();

    public DbSet<SettlementJob> SettlementJobs => Set<SettlementJob>();

    public DbSet<MoneyJournalRow> MoneyJournals => Set<MoneyJournalRow>();

    public DbSet<Payout> Payouts => Set<Payout>();

    public DbSet<CampaignPayee> CampaignPayees => Set<CampaignPayee>();

    public DbSet<ReconciliationDifferenceRow> ReconciliationDifferences => Set<ReconciliationDifferenceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.MapIdempotencyKeys();
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
            // One setup per pledge: the handler finds or inserts by it.
            setup.HasIndex(s => s.PledgeId).IsUnique();
            setup.Property(s => s.Status)
                .HasConversion(s => s.ToString().ToUpperInvariant(), s => Enum.Parse<CardSetupStatus>(s, true))
                .HasMaxLength(20);
            setup.Property(s => s.PaymentMethodRef).HasMaxLength(200);
            setup.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<Charge>(charge =>
        {
            charge.ToTable("charges", t => t.HasCheckConstraint("charges_amount_positive", "amount_minor > 0"));
            charge.HasKey(c => c.PledgeId);
            charge.Property(c => c.PledgeId).ValueGeneratedNever();
            charge.Property(c => c.Currency).HasMaxLength(3).IsFixedLength();
            charge.Property(c => c.PaymentMethodRef).HasMaxLength(200);
            charge.Property(c => c.PspPaymentId).HasMaxLength(100);
            charge.Property(c => c.PspRefundId).HasMaxLength(100);
            charge.Property(c => c.Status).HasConversion(s => Names.Of(s), s => Names.Parse<ChargeStatus>(s)).HasMaxLength(20);
            charge.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            charge.Property(c => c.LastDeclineCode).HasMaxLength(100);
            charge.Property(c => c.LastAdviceCode).HasMaxLength(100);
            charge.HasIndex(c => c.CampaignId).HasFilter("status IN ('TO_CHARGE', 'CHARGING', 'PAYMENT_FAILED')").HasDatabaseName("charges_open");
        });

        modelBuilder.Entity<ChargeAttempt>(attempt =>
        {
            attempt.ToTable("charge_attempts", t =>
            {
                // The deadline's charge, three retries and a final one (lesson s3-a04).
                t.HasCheckConstraint("charge_attempts_no", "attempt_no BETWEEN 1 AND 5");
                t.HasCheckConstraint("charge_attempts_status", "status IN ('IN_FLIGHT', 'SUCCEEDED', 'FAILED')");
            });
            attempt.HasKey(a => new { a.PledgeId, a.AttemptNo });
            attempt.Property(a => a.IdempotencyKey).HasMaxLength(200);
            attempt.HasIndex(a => a.IdempotencyKey).IsUnique();
            attempt.Property(a => a.Status).HasConversion(s => Names.Of(s), s => Names.Parse<AttemptStatus>(s)).HasMaxLength(20);
            attempt.Property(a => a.PspPaymentId).HasMaxLength(100);
            attempt.Property(a => a.DeclineCode).HasMaxLength(100);
            attempt.Property(a => a.AdviceCode).HasMaxLength(100);
            attempt.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            // Lesson 8: at most one successful attempt per pledge, whatever any worker does.
            attempt.HasIndex(a => a.PledgeId).IsUnique().HasFilter("status = 'SUCCEEDED'").HasDatabaseName("one_success_per_pledge");
            // A webhook about a payment the backer confirmed finds its attempt by the PSP's id.
            attempt.HasIndex(a => a.PspPaymentId).HasFilter("psp_payment_id IS NOT NULL").HasDatabaseName("charge_attempts_by_payment");
        });

        modelBuilder.Entity<SettlementJob>(job =>
        {
            job.ToTable("settlement_jobs");
            job.HasKey(j => j.CampaignId);
            job.Property(j => j.CampaignId).ValueGeneratedNever();
            job.Property(j => j.Status).HasConversion(s => Names.Of(s), s => Names.Parse<SettlementStatus>(s)).HasMaxLength(20);
            job.Property(j => j.LeaseOwner).HasMaxLength(100);
            job.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
            job.HasIndex(j => j.CreatedAt).HasFilter("status <> 'DONE'").HasDatabaseName("settlement_jobs_open");
        });

        // The money ledger. Lines are added, never changed; a journal's lines sum to zero, which a
        // deferred constraint trigger checks at commit (added by the migration as SQL).
        modelBuilder.Entity<MoneyJournalRow>(journal =>
        {
            journal.ToTable("money_journals");
            journal.HasKey(j => j.JournalId);
            journal.Property(j => j.JournalId).UseIdentityAlwaysColumn();
            journal.Property(j => j.Kind).HasMaxLength(20);
            journal.Property(j => j.SourceRef).HasMaxLength(200);
            journal.Property(j => j.Currency).HasMaxLength(3).IsFixedLength();
            journal.Property(j => j.CreatedAt).HasDefaultValueSql("now()");
            journal.HasIndex(j => new { j.SourceRef, j.Kind }).IsUnique().HasDatabaseName("one_journal_per_source");
            journal.HasIndex(j => j.CampaignId);
        });

        modelBuilder.Entity<MoneyEntryRow>(entry =>
        {
            entry.ToTable("money_entries", t => t.HasCheckConstraint("money_entries_not_zero", "amount_minor <> 0"));
            entry.HasKey(e => e.EntryId);
            entry.Property(e => e.EntryId).UseIdentityAlwaysColumn();
            entry.Property(e => e.Account).HasMaxLength(40);
            entry.HasOne<MoneyJournalRow>().WithMany().HasForeignKey(e => e.JournalId);
        });

        modelBuilder.Entity<Payout>(payout =>
        {
            payout.ToTable("payouts");
            payout.HasKey(p => p.CampaignId);
            payout.Property(p => p.CampaignId).ValueGeneratedNever();
            payout.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
            payout.Property(p => p.Status).HasConversion(s => Names.Of(s), s => Names.Parse<PayoutStatus>(s)).HasMaxLength(20);
            payout.Property(p => p.PspTransferId).HasMaxLength(100);
            payout.Property(p => p.SettledAt).HasDefaultValueSql("now()");
            payout.HasIndex(p => p.SettledAt).HasFilter("status <> 'PAID_OUT'").HasDatabaseName("payouts_open");
        });

        modelBuilder.Entity<ReconciliationDifferenceRow>(difference =>
        {
            difference.ToTable("reconciliation_differences");
            difference.HasKey(d => new { d.CampaignId, d.SourceRef, d.Kind });
            difference.Property(d => d.SourceRef).HasMaxLength(200);
            difference.Property(d => d.Kind).HasMaxLength(30);
            difference.Property(d => d.FoundAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CampaignPayee>(payee =>
        {
            payee.ToTable("campaign_payees");
            payee.HasKey(p => p.CampaignId);
            payee.Property(p => p.CampaignId).ValueGeneratedNever();
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

/// <summary>Statuses as the database spells them: <c>ToCharge</c> is <c>TO_CHARGE</c>.</summary>
internal static class Names
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(text.Replace("_", "", StringComparison.Ordinal), ignoreCase: true);
}
