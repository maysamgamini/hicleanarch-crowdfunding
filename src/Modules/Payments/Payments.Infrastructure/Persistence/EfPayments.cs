using Microsoft.EntityFrameworkCore;

using Payments.Application;
using Payments.Domain;

namespace Payments.Infrastructure.Persistence;

internal sealed class EfPayments(PaymentsDbContext db) : IPspCustomers, ICardSetups, IWebhookInbox
{
    public Task<string?> Find(Guid backerId, CancellationToken cancellationToken) =>
        db.Customers.AsNoTracking().Where(c => c.BackerId == backerId).Select(c => c.PspCustomerId).SingleOrDefaultAsync(cancellationToken);

    public async Task<string> Add(PspCustomer customer, CancellationToken cancellationToken)
    {
        // Two first setups for one backer may race; the first customer recorded is the one kept.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO payments.psp_customers (backer_id, psp_customer_id) VALUES ({customer.BackerId}, {customer.PspCustomerId})
            ON CONFLICT (backer_id) DO NOTHING
            """, cancellationToken);
        return (await Find(customer.BackerId, cancellationToken))!;
    }

    public async Task Add(CardSetup setup, CancellationToken cancellationToken)
    {
        db.CardSetups.Add(setup);
        await db.SaveChangesAsync(cancellationToken);
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
