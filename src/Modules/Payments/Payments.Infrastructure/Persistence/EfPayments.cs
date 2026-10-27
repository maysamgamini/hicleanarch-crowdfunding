using Microsoft.EntityFrameworkCore;

using Payments.Application;
using Payments.Domain;
using Payments.Infrastructure.Tracing;

namespace Payments.Infrastructure.Persistence;

internal sealed class EfPayments(PaymentsDbContext db) : IPspCustomers, ICardSetups, IWebhookInbox
{
    public Task<string?> Find(Guid backerId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().Where(c => c.BackerId == backerId).Select(c => c.PspCustomerId).SingleOrDefaultAsync(cancellationToken);

    public async Task<string> Add(PspCustomer customer, CancellationToken cancellationToken)
    {
        // Two first setups for one backer may race; the first customer recorded is the one kept. The
        // racers carry the same customer id (the PSP call had the backer's key), so a conflict on
        // either unique column means the row is already there.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.psp_customers (backer_id, psp_customer_id) VALUES ({customer.BackerId}, {customer.PspCustomerId})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        return (await Find(customer.BackerId, cancellationToken))!;
    }

    public async Task<CardSetup> Add(CardSetup setup, CancellationToken cancellationToken)
    {
        // One row per pledge (a unique index): a repeat, or a racing twin, keeps the first row. Both
        // carry the PSP's same setup id, since the PSP call had the pledge's key, so a conflict on
        // either unique column means the row is already there.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.card_setups (id, pledge_id, backer_id, psp_setup_id, status, trace_parent)
            VALUES ({setup.Id}, {setup.PledgeId}, {setup.BackerId}, {setup.PspSetupId}, 'STARTED', {CreationTrace.Current})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        var kept = await db.CardSetups.AsNoTracking().SingleAsync(s => s.PledgeId == setup.PledgeId, cancellationToken);
        if (kept.PspSetupId == setup.PspSetupId || kept.Status != CardSetupStatus.Started)
        {
            return kept;
        }

        // A row from before setups carried the pledge's key names another setup: the form whose
        // secret the backer now holds is the one recorded, so its webhook finds the row.
        return (await db.CardSetups.FromSql($"""
            UPDATE payments.card_setups SET psp_setup_id = {setup.PspSetupId}
            WHERE pledge_id = {setup.PledgeId} AND status = 'STARTED'
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken)).SingleOrDefault() ?? kept;
    }

    public async Task<CardSetup?> TryComplete(string pspSetupId, string paymentMethodRef, CancellationToken cancellationToken) =>
        (await db.CardSetups.FromSql($"""
            UPDATE payments.card_setups SET status = 'SUCCEEDED', payment_method_ref = {paymentMethodRef}
            WHERE psp_setup_id = {pspSetupId} AND status = 'STARTED'
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken)).SingleOrDefault();

    public async Task<bool> TryFail(string pspSetupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payments.card_setups SET status = 'FAILED' WHERE psp_setup_id = {pspSetupId} AND status = 'STARTED'
            """, cancellationToken) == 1;

    public async Task<bool> TryRecord(string messageId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.webhook_inbox (message_id) VALUES ({messageId}) ON CONFLICT DO NOTHING
            """, cancellationToken) == 1;
}
