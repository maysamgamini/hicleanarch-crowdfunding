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

    public async Task<Guid?> Claim(string worker, TimeSpan lease, Guid? campaignId, IReadOnlyCollection<Guid>? among, CancellationToken cancellationToken)
    {
        var only = campaignId ?? Guid.Empty;
        var any = campaignId is null;
        var everywhere = among is null;
        Guid[] chosen = [.. among ?? []];
        return (await db.Database.SqlQuery<Guid>($"""
            UPDATE payments.settlement_jobs
            SET status = 'RUNNING', lease_owner = {worker}, lease_until = now() + {lease}, not_before = NULL
            WHERE campaign_id = (
                SELECT campaign_id FROM payments.settlement_jobs
                WHERE status <> 'DONE' AND (lease_until IS NULL OR lease_until < now())
                  AND (({any} AND (not_before IS NULL OR not_before <= now()) AND ({everywhere} OR campaign_id = ANY({chosen})))
                       OR campaign_id = {only})   -- a named job is claimed at once
                ORDER BY last_turn_at NULLS FIRST, created_at
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

    public Task Release(Guid campaignId, string worker, TimeSpan unknownBackoff, TimeSpan pause, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs j
            SET status = 'PENDING', lease_owner = NULL, lease_until = NULL, last_turn_at = clock_timestamp(),
                not_before = greatest(now() + {pause}, CASE
                    WHEN EXISTS (SELECT 1 FROM payments.charges c
                                 WHERE c.campaign_id = j.campaign_id AND c.status IN ('TO_CHARGE', 'PAYMENT_FAILED')
                                   AND (c.next_attempt_at IS NULL OR c.next_attempt_at <= now()))
                        THEN NULL                       -- work is waiting now
                    WHEN EXISTS (SELECT 1 FROM payments.charges c WHERE c.campaign_id = j.campaign_id AND c.status = 'CHARGING')
                      OR (SELECT count(*) FROM payments.charges c WHERE c.campaign_id = j.campaign_id) < j.expected_charges
                        THEN LEAST(                     -- an unknown attempt, or charges still to be created
                            (SELECT min(c.next_attempt_at) FROM payments.charges c
                             WHERE c.campaign_id = j.campaign_id AND c.status IN ('TO_CHARGE', 'PAYMENT_FAILED')),
                            now() + {unknownBackoff})
                    ELSE COALESCE(                      -- only retries, each due at its own time
                        (SELECT min(c.next_attempt_at) FROM payments.charges c
                         WHERE c.campaign_id = j.campaign_id AND c.status IN ('TO_CHARGE', 'PAYMENT_FAILED')),
                        now() + {unknownBackoff})
                END)
            WHERE j.campaign_id = {campaignId} AND j.lease_owner = {worker} AND j.status = 'RUNNING'
            """, cancellationToken);

    public Task ReleaseAll(string worker, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs SET status = 'PENDING', lease_owner = NULL, lease_until = NULL
            WHERE lease_owner = {worker} AND status = 'RUNNING'
            """, cancellationToken);

    public async Task<bool> TryFinish(Guid campaignId, string worker, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.settlement_jobs SET status = 'DONE', lease_owner = NULL, lease_until = NULL
            WHERE campaign_id = {campaignId} AND lease_owner = {worker} AND status = 'RUNNING'
            """, cancellationToken) == 1;

    public async Task<IReadOnlyList<ChargeWork>> Workable(Guid campaignId, int batchSize, CancellationToken cancellationToken)
    {
        // A charge in its recovery window is due again at its next attempt's time (lesson s3-a04).
        var rows = await db.Database.SqlQuery<ChargeRow>($"""
            SELECT c.*, coalesce(a.on_session, false) AS on_session
            FROM payments.charges c
            LEFT JOIN payments.charge_attempts a ON a.pledge_id = c.pledge_id AND a.attempt_no = c.attempt_no
            WHERE c.campaign_id = {campaignId}
              AND (c.status = 'CHARGING'
                   OR (c.status IN ('TO_CHARGE', 'PAYMENT_FAILED') AND (c.next_attempt_at IS NULL OR c.next_attempt_at <= now())))
            ORDER BY c.status = 'CHARGING' DESC, c.created_at
            LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(r => r.ToWork())];
    }

    public async Task<ChargeOfPledge?> FindCharge(Guid pledgeId, CancellationToken cancellationToken)
    {
        var row = (await db.Database.SqlQuery<ChargeRow>($"""
            SELECT c.*, coalesce(a.on_session, false) AS on_session
            FROM payments.charges c
            LEFT JOIN payments.charge_attempts a ON a.pledge_id = c.pledge_id AND a.attempt_no = c.attempt_no
            WHERE c.pledge_id = {pledgeId}
            """).ToListAsync(cancellationToken)).SingleOrDefault();
        return row is null ? null : new ChargeOfPledge(row.CampaignId, row.ToWork());
    }

    public async Task<ChargeWork?> FindForBacker(Guid pledgeId, Guid backerId, CancellationToken cancellationToken) =>
        (await db.Database.SqlQuery<ChargeRow>($"""
            SELECT c.*, coalesce(a.on_session, false) AS on_session
            FROM payments.charges c
            LEFT JOIN payments.charge_attempts a ON a.pledge_id = c.pledge_id AND a.attempt_no = c.attempt_no
            WHERE c.pledge_id = {pledgeId} AND c.backer_id = {backerId}
            """).ToListAsync(cancellationToken)).SingleOrDefault()?.ToWork();

    public async Task<(ChargeWork Charge, int AttemptNo)?> FindByPayment(string pspPaymentId, CancellationToken cancellationToken)
    {
        var row = (await db.Database.SqlQuery<ChargeRow>($"""
            SELECT c.*, a.on_session
            FROM payments.charge_attempts a
            JOIN payments.charges c ON c.pledge_id = a.pledge_id
            WHERE a.psp_payment_id = {pspPaymentId} AND a.status = 'IN_FLIGHT' AND c.attempt_no = a.attempt_no
            """).ToListAsync(cancellationToken)).SingleOrDefault();
        return row is null ? null : (row.ToWork(), row.AttemptNo);
    }

    public async Task<bool> TryUseNewCard(Guid pledgeId, string paymentMethodRef, CancellationToken cancellationToken)
    {
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges SET payment_method_ref = {paymentMethodRef}, next_attempt_at = now(), last_decline_code = NULL, last_advice_code = NULL
            WHERE pledge_id = {pledgeId} AND status = 'PAYMENT_FAILED'
            """, cancellationToken);
        if (changed == 1)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE payments.settlement_jobs SET not_before = now()
                WHERE campaign_id = (SELECT campaign_id FROM payments.charges WHERE pledge_id = {pledgeId}) AND status <> 'DONE'
                """, cancellationToken);
        }

        return changed == 1;
    }

    public Task RecordPayment(Guid pledgeId, int attemptNo, string pspPaymentId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charge_attempts SET psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND attempt_no = {attemptNo} AND status = 'IN_FLIGHT';
            UPDATE payments.charges SET psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND attempt_no = {attemptNo} AND status = 'CHARGING';
            """, cancellationToken);

    public async Task<SettlementCounts> Counts(Guid campaignId, CancellationToken cancellationToken)
    {
        var expected = await db.SettlementJobs.Where(j => j.CampaignId == campaignId).Select(j => j.ExpectedCharges).SingleAsync(cancellationToken);
        var byStatus = await db.Charges.Where(c => c.CampaignId == campaignId)
            .GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        int Of(ChargeStatus s) => byStatus.GetValueOrDefault(s);
        return new SettlementCounts(expected, Of(ChargeStatus.Charged), Of(ChargeStatus.Dropped),
            Of(ChargeStatus.ToCharge) + Of(ChargeStatus.Charging) + Of(ChargeStatus.PaymentFailed));
    }

    public async Task<(SettlementCounts Counts, string Status, int OpenAheadOrAlongside)?> Progress(Guid campaignId, CancellationToken cancellationToken)
    {
        var job = await db.SettlementJobs.AsNoTracking().SingleOrDefaultAsync(j => j.CampaignId == campaignId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var counts = await Counts(campaignId, cancellationToken);
        // The ETA is for the charges due now; one in its recovery window waits for its day, not for the rate.
        var open = await db.Charges.CountAsync(c => c.CampaignId == campaignId && (c.Status == ChargeStatus.ToCharge || c.Status == ChargeStatus.Charging), cancellationToken);
        var shared = await db.Database.SqlQuery<int>($"""
            SELECT coalesce(sum(least(c.open, {open})), 0)::int AS "Value"
            FROM (SELECT campaign_id, count(*) AS open FROM payments.charges
                  WHERE status IN ('TO_CHARGE', 'CHARGING') GROUP BY campaign_id) c
            JOIN payments.settlement_jobs j ON j.campaign_id = c.campaign_id AND j.status <> 'DONE'
            """).SingleAsync(cancellationToken);
        return (counts, Names.Of(job.Status), shared);
    }

    public async Task<int?> TryStartAttempt(Guid pledgeId, string pledgePublicId, bool onSession, CancellationToken cancellationToken)
    {
        // Its own transaction, committed before the PSP is called; inside a backer's request, that request's.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var started = await db.Database.SqlQuery<int>($"""
            UPDATE payments.charges SET status = 'CHARGING', attempt_no = attempt_no + 1, next_attempt_at = NULL, psp_payment_id = NULL
            WHERE pledge_id = {pledgeId} AND status IN ('TO_CHARGE', 'PAYMENT_FAILED')
            RETURNING attempt_no AS "Value"
            """).ToListAsync(cancellationToken);
        if (started.Count == 0)
        {
            return null; // another worker has it
        }

        var n = started[0];
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.charge_attempts (pledge_id, attempt_no, idempotency_key, status, on_session)
            VALUES ({pledgeId}, {n}, {Charge.KeyFor(pledgePublicId, n)}, 'IN_FLIGHT', {onSession})
            """, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken); // recorded before the PSP is called
        }
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

    public async Task<bool> TryRecordDecline(Guid pledgeId, int attemptNo, string code, string? adviceCode, string? pspPaymentId, bool drop, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken)
    {
        var attempt = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charge_attempts SET status = 'FAILED', decline_code = {code}, advice_code = {adviceCode}, psp_payment_id = {pspPaymentId}
            WHERE pledge_id = {pledgeId} AND attempt_no = {attemptNo} AND status = 'IN_FLIGHT'
            """, cancellationToken);
        var status = drop ? "DROPPED" : "PAYMENT_FAILED";
        var charge = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges
            SET status = {status}, next_attempt_at = {nextAttemptAt}, last_decline_code = {code}, last_advice_code = {adviceCode},
                payment_failed_at = coalesce(payment_failed_at, now())
            WHERE pledge_id = {pledgeId} AND status = 'CHARGING' AND attempt_no = {attemptNo}
            """, cancellationToken);
        return attempt == 1 && charge == 1;
    }

    public async Task<bool> TryDropWaiting(Guid pledgeId, int attemptNo, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.charges SET status = 'DROPPED', next_attempt_at = NULL
            WHERE pledge_id = {pledgeId} AND status = 'PAYMENT_FAILED' AND attempt_no = {attemptNo}
            """, cancellationToken) == 1;

    public async Task<string> CustomerOf(Guid backerId, CancellationToken cancellationToken) =>
        await customers.Find(backerId, cancellationToken)
            ?? await customers.Add(PspCustomer.For(backerId, await psp.CreateCustomer(backerId, cancellationToken)), cancellationToken);
}

/// <summary>A charge row with its latest attempt's kind, as raw SQL returns it.</summary>
internal sealed record ChargeRow(
    Guid PledgeId,
    Guid CampaignId,
    Guid BackerId,
    long AmountMinor,
    string Currency,
    string PaymentMethodRef,
    string Status,
    int AttemptNo,
    string? PspPaymentId,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? PaymentFailedAt,
    string? LastDeclineCode,
    string? LastAdviceCode,
    DateTimeOffset CreatedAt,
    bool OnSession)
{
    public ChargeWork ToWork() => new(
        PledgeId, BackerId, AmountMinor, Currency, PaymentMethodRef, Names.Parse<ChargeStatus>(Status), AttemptNo, PspPaymentId,
        CampaignId, PaymentFailedAt, LastDeclineCode, OnSession, LastAdviceCode);
}
