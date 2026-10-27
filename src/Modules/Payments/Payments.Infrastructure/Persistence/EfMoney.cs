using Microsoft.EntityFrameworkCore;

using Payments.Application;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

/// <summary>The money ledger, payouts, payees and refunds, in the payments schema.</summary>
internal sealed class EfMoney(PaymentsDbContext db) : IMoneyLedger, IPayouts, ICampaignPayees, IChargeRefunds, IChargeJournals
{
    public async Task<bool> Post(MoneyJournal journal, CancellationToken cancellationToken)
    {
        var posted = await db.Database.SqlQuery<long>($"""
            INSERT INTO payments.money_journals (campaign_id, kind, source_ref, currency)
            VALUES ({journal.CampaignId}, {journal.Kind}, {journal.SourceRef}, {journal.Currency})
            ON CONFLICT (source_ref, kind) DO NOTHING
            RETURNING journal_id AS "Value"
            """).ToListAsync(cancellationToken);
        if (posted.Count == 0)
        {
            return false; // a replay: this journal was posted before
        }

        var accounts = journal.Lines.Select(l => l.Account).ToArray();
        var amounts = journal.Lines.Select(l => l.AmountMinor).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.money_entries (journal_id, account, amount_minor)
            SELECT {posted[0]}, line.account, line.amount_minor
            FROM unnest({accounts}::text[], {amounts}::bigint[]) AS line(account, amount_minor)
            """, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, long>> Balances(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<AccountBalance>($"""
            SELECT e.account, sum(e.amount_minor)::bigint AS amount_minor
            FROM payments.money_entries e
            JOIN payments.money_journals j ON j.journal_id = e.journal_id
            WHERE j.campaign_id = {campaignId}
            GROUP BY e.account
            """).ToDictionaryAsync(b => b.Account, b => b.AmountMinor, cancellationToken);

    public Task<bool> HasJournal(Guid campaignId, string kind, CancellationToken cancellationToken) =>
        db.MoneyJournals.AnyAsync(j => j.CampaignId == campaignId && j.Kind == kind, cancellationToken);

    public Task Open(Guid campaignId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.payouts (campaign_id, currency, status)
            SELECT {campaignId}, c.currency, 'SETTLED' FROM payments.charges c WHERE c.campaign_id = {campaignId} LIMIT 1
            ON CONFLICT (campaign_id) DO NOTHING
            """, cancellationToken);

    public Task<Payout?> Find(Guid campaignId, CancellationToken cancellationToken) =>
        db.Payouts.AsNoTracking().SingleOrDefaultAsync(p => p.CampaignId == campaignId, cancellationToken);

    public Task<Payout?> Lock(Guid campaignId, CancellationToken cancellationToken) =>
        db.Payouts.FromSql($"SELECT * FROM payments.payouts WHERE campaign_id = {campaignId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<UnbookedCharge>> Unbooked(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<UnbookedRow>($"""
            SELECT c.pledge_id, c.campaign_id, c.amount_minor, c.currency, c.psp_payment_id, u.psp_customer_id, c.attempt_no
            FROM payments.charges c
            JOIN payments.psp_customers u ON u.backer_id = c.backer_id
            WHERE c.campaign_id = {campaignId} AND c.status IN ('CHARGED', 'REFUNDED') AND c.psp_payment_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM payments.money_journals j WHERE j.source_ref = c.psp_payment_id AND j.kind = 'CHARGE')
            """).Select(r => new UnbookedCharge(r.PledgeId, r.CampaignId, r.AmountMinor, r.Currency, r.PspPaymentId, r.PspCustomerId, r.AttemptNo))
            .ToListAsync(cancellationToken);

    public async Task<bool> TrySetAmounts(Guid campaignId, long amountMinor, long reserveMinor, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.payouts SET amount_minor = {amountMinor}, reserve_minor = {reserveMinor}
            WHERE campaign_id = {campaignId} AND amount_minor IS NULL
            """, cancellationToken) == 1;

    public async Task<bool> TryMarkPaidOut(Guid campaignId, PayoutStatus from, string? pspTransferId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.payouts SET status = 'PAID_OUT', psp_transfer_id = {pspTransferId}, paid_out_at = now()
            WHERE campaign_id = {campaignId} AND status = {from.Name()}
            """, cancellationToken) == 1;

    public Task Record(Guid campaignId, Guid creatorId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.campaign_payees (campaign_id, creator_id) VALUES ({campaignId}, {creatorId})
            ON CONFLICT (campaign_id) DO NOTHING
            """, cancellationToken);

    async Task<Guid?> ICampaignPayees.Find(Guid campaignId, CancellationToken cancellationToken) =>
        await db.CampaignPayees.Where(p => p.CampaignId == campaignId).Select(p => (Guid?)p.CreatorId).SingleOrDefaultAsync(cancellationToken);

    async Task<RefundableCharge?> IChargeRefunds.Find(Guid pledgeId, CancellationToken cancellationToken) =>
        await db.Charges.AsNoTracking().Where(c => c.PledgeId == pledgeId)
            .Select(c => new RefundableCharge(c.PledgeId, c.CampaignId, c.AmountMinor, c.Currency, c.Status, c.PspPaymentId, c.PspRefundId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryMarkRefunded(Guid pledgeId, string pspRefundId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges SET status = 'REFUNDED', psp_refund_id = {pspRefundId}
            WHERE pledge_id = {pledgeId} AND status = 'CHARGED'
            """, cancellationToken) == 1;

    private sealed class AccountBalance
    {
        public string Account { get; set; } = "";

        public long AmountMinor { get; set; }
    }
}

/// <summary>A money journal's head row; its lines are <see cref="MoneyEntryRow"/>. Written by SQL, read by queries.</summary>
internal sealed class MoneyJournalRow
{
    public long JournalId { get; init; }

    public Guid CampaignId { get; init; }

    public required string Kind { get; init; }

    public required string SourceRef { get; init; }

    public required string Currency { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class MoneyEntryRow
{
    public long EntryId { get; init; }

    public long JournalId { get; init; }

    public required string Account { get; init; }

    public long AmountMinor { get; init; }
}

/// <summary>A campaign's creator, as Campaigns published it: whom Payments pays.</summary>
internal sealed class CampaignPayee
{
    public Guid CampaignId { get; init; }

    public Guid CreatorId { get; init; }
}

/// <summary>A charge without its CHARGE journal, as the query reads it.</summary>
internal sealed class UnbookedRow
{
    public Guid PledgeId { get; init; }

    public Guid CampaignId { get; init; }

    public long AmountMinor { get; init; }

    public required string Currency { get; init; }

    public required string PspPaymentId { get; init; }

    public required string PspCustomerId { get; init; }

    public int AttemptNo { get; init; }
}
