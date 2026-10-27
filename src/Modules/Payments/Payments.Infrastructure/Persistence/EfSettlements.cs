using Microsoft.EntityFrameworkCore;

using Payments.Application;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

internal sealed class EfSettlements(PaymentsDbContext db, IPaymentProvider psp, IPspCustomers customers) : ISettlements
{
    public Task CreateCharge(Charge charge, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.charges (pledge_id, campaign_id, backer_id, amount_minor, currency, payment_method_ref, status, attempt_no)
            VALUES ({charge.PledgeId}, {charge.CampaignId}, {charge.BackerId}, {charge.AmountMinor}, {charge.Currency}, {charge.PaymentMethodRef}, 'TO_CHARGE', 0)
            ON CONFLICT (pledge_id) DO NOTHING
            """, cancellationToken);

    public Task CreateJob(Guid campaignId, int expectedCharges, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.settlement_jobs (campaign_id, status, expected_charges) VALUES ({campaignId}, 'PENDING', {expectedCharges})
            ON CONFLICT (campaign_id) DO NOTHING
            """, cancellationToken);

    public async Task<Guid?> Claim(string worker, TimeSpan lease, Guid? campaignId, CancellationToken cancellationToken)
    {
        var only = campaignId ?? Guid.Empty;
        var any = campaignId is null;
        return (await db.Database.SqlQuery<Guid>($"""
            UPDATE payments.settlement_jobs
            SET status = 'RUNNING', lease_owner = {worker}, lease_until = now() + {lease}, not_before = NULL
            WHERE campaign_id = (
                SELECT campaign_id FROM payments.settlement_jobs
                WHERE status <> 'DONE' AND (lease_until IS NULL OR lease_until < now())
                  AND (NOT {any} OR not_before IS NULL OR not_before <= now())   -- a named job is claimed at once
                  AND ({any} OR campaign_id = {only})
                ORDER BY created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING campaign_id AS "Value"
            """).ToListAsync(cancellationToken)).Select(id => (Guid?)id).SingleOrDefault();
    }

    public async Task<bool> Renew(Guid campaignId, string worker, TimeSpan lease, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs SET lease_until = now() + {lease}
            WHERE campaign_id = {campaignId} AND lease_owner = {worker} AND status = 'RUNNING'
            """, cancellationToken) == 1;

    public Task Release(Guid campaignId, string worker, TimeSpan recheckAfter, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs j
            SET status = 'PENDING', lease_owner = NULL, lease_until = NULL,
                not_before = CASE
                    WHEN EXISTS (SELECT 1 FROM payments.charges c
                                 WHERE c.campaign_id = j.campaign_id AND c.status = 'TO_CHARGE'
                                   AND (c.next_attempt_at IS NULL OR c.next_attempt_at <= now()))
                        THEN NULL                       -- work is waiting now
                    WHEN EXISTS (SELECT 1 FROM payments.charges c WHERE c.campaign_id = j.campaign_id AND c.status = 'CHARGING')
                      OR (SELECT count(*) FROM payments.charges c WHERE c.campaign_id = j.campaign_id) < j.expected_charges
                        THEN LEAST(                     -- an unknown attempt, or charges still to be created
                            (SELECT min(c.next_attempt_at) FROM payments.charges c
                             WHERE c.campaign_id = j.campaign_id AND c.status = 'TO_CHARGE'),
                            now() + {recheckAfter})
                    ELSE COALESCE(                      -- only retries, each due at its own time
                        (SELECT min(c.next_attempt_at) FROM payments.charges c
                         WHERE c.campaign_id = j.campaign_id AND c.status = 'TO_CHARGE'),
                        now() + {recheckAfter})
                END
            WHERE j.campaign_id = {campaignId} AND j.lease_owner = {worker} AND j.status = 'RUNNING'
            """, cancellationToken);

    public async Task<bool> TryFinish(Guid campaignId, string worker, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs SET status = 'DONE', lease_owner = NULL, lease_until = NULL
            WHERE campaign_id = {campaignId} AND lease_owner = {worker} AND status = 'RUNNING'
            """, cancellationToken) == 1;

    public async Task<IReadOnlyList<ChargeWork>> Workable(Guid campaignId, CancellationToken cancellationToken)
    {
        var rows = await db.Charges.FromSql($"""
            SELECT * FROM payments.charges
            WHERE campaign_id = {campaignId}
              AND (status = 'CHARGING' OR (status = 'TO_CHARGE' AND (next_attempt_at IS NULL OR next_attempt_at <= now())))
            ORDER BY created_at
            """).AsNoTracking().ToListAsync(cancellationToken);
        return [.. rows.Select(c => new ChargeWork(c.PledgeId, c.BackerId, c.AmountMinor, c.Currency, c.PaymentMethodRef, c.Status, c.AttemptNo, c.PspPaymentId))];
    }

    public async Task<ChargeOfPledge?> FindCharge(Guid pledgeId, CancellationToken cancellationToken)
    {
        var c = await db.Charges.AsNoTracking().SingleOrDefaultAsync(c => c.PledgeId == pledgeId, cancellationToken);
        return c is null
            ? null
            : new ChargeOfPledge(c.CampaignId, new ChargeWork(c.PledgeId, c.BackerId, c.AmountMinor, c.Currency, c.PaymentMethodRef, c.Status, c.AttemptNo, c.PspPaymentId));
    }

    public async Task<SettlementCounts> Counts(Guid campaignId, CancellationToken cancellationToken)
    {
        var expected = await db.SettlementJobs.Where(j => j.CampaignId == campaignId).Select(j => j.ExpectedCharges).SingleAsync(cancellationToken);
        var byStatus = await db.Charges.Where(c => c.CampaignId == campaignId)
            .GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        int Of(ChargeStatus s) => byStatus.GetValueOrDefault(s);
        return new SettlementCounts(expected, Of(ChargeStatus.Charged), Of(ChargeStatus.Failed), Of(ChargeStatus.ToCharge) + Of(ChargeStatus.Charging));
    }

    public async Task<int?> TryStartAttempt(Guid pledgeId, string pledgePublicId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var started = await db.Database.SqlQuery<int>($"""
            UPDATE payments.charges SET status = 'CHARGING', attempt_no = attempt_no + 1, next_attempt_at = NULL
            WHERE pledge_id = {pledgeId} AND status = 'TO_CHARGE'
            RETURNING attempt_no AS "Value"
            """).ToListAsync(cancellationToken);
        if (started.Count == 0)
        {
            return null; // another worker has it
        }

        var n = started[0];
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.charge_attempts (pledge_id, attempt_no, idempotency_key, status)
            VALUES ({pledgeId}, {n}, {Charge.KeyFor(pledgePublicId, n)}, 'IN_FLIGHT')
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken); // recorded before the PSP is called
        return n;
    }

    public async Task<bool> TryRecordSuccess(Guid pledgeId, int attemptNo, string pspPaymentId, CancellationToken cancellationToken)
    {
        var attempt = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charge_attempts SET status = 'SUCCEEDED', psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND attempt_no = {attemptNo} AND status = 'IN_FLIGHT'
            """, cancellationToken);
        var charge = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges SET status = 'CHARGED', psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND status = 'CHARGING' AND attempt_no = {attemptNo}
            """, cancellationToken);
        return attempt == 1 && charge == 1;
    }

    public async Task<bool> TryRecordDecline(Guid pledgeId, int attemptNo, string code, string? pspPaymentId, bool final, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken)
    {
        var attempt = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charge_attempts SET status = 'FAILED', decline_code = {code}, psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND attempt_no = {attemptNo} AND status = 'IN_FLIGHT'
            """, cancellationToken);
        var status = final ? "FAILED" : "TO_CHARGE";
        var charge = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges SET status = {status}, next_attempt_at = {nextAttemptAt}
            WHERE pledge_id = {pledgeId} AND status = 'CHARGING' AND attempt_no = {attemptNo}
            """, cancellationToken);
        return attempt == 1 && charge == 1;
    }

    public async Task<string> CustomerOf(Guid backerId, CancellationToken cancellationToken) =>
        await customers.Find(backerId, cancellationToken)
            ?? await customers.Add(PspCustomer.For(backerId, await psp.CreateCustomer(backerId, cancellationToken)), cancellationToken);
}
