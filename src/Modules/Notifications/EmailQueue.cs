using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Notifications;

/// <summary>An email a worker has claimed, with the attempt this is.</summary>
internal sealed record ClaimedEmail(Guid EventId, Guid RecipientId, string Kind, string Subject, string Body, int Attempts);

/// <summary>
/// The rows of <c>outgoing_emails</c> as the workers see them. Every statement is short and stands
/// alone, so no transaction stays open while an email is sent. A claim moves the row's next attempt a
/// lease ahead: a worker that dies leaves its rows to be claimed again once the lease runs out, so an
/// email is sent at least once, and twice only if a task dies between the send and its mark.
/// </summary>
internal sealed class EmailQueue(NotificationsDbContext db)
{
    // SKIP LOCKED: workers on several tasks claim different rows, and never wait for each other.
    public async Task<IReadOnlyList<ClaimedEmail>> Claim(int batchSize, TimeSpan lease, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<ClaimedEmail>($"""
            WITH due AS (
                SELECT event_id, recipient_id, kind FROM notifications.outgoing_emails
                WHERE status = 'PENDING' AND next_attempt_at <= now()
                ORDER BY next_attempt_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED)
            UPDATE notifications.outgoing_emails o SET attempts = o.attempts + 1, next_attempt_at = now() + {lease}
            FROM due WHERE o.event_id = due.event_id AND o.recipient_id = due.recipient_id AND o.kind = due.kind
            RETURNING o.event_id, o.recipient_id, o.kind, o.subject, o.body, o.attempts
            """).ToListAsync(cancellationToken);

    public Task Sent(ClaimedEmail email, CancellationToken cancellationToken) =>
        Finish(email, "SENT", null, cancellationToken);

    /// <summary>The user no longer exists: there is nobody to tell, and nothing to retry.</summary>
    public Task NoAddress(ClaimedEmail email, CancellationToken cancellationToken) =>
        Finish(email, "NO_ADDRESS", null, cancellationToken);

    /// <summary>This recipient's send failed: tried again after <paramref name="retryAfter"/>, or given up.</summary>
    public Task Failed(ClaimedEmail email, TimeSpan retryAfter, bool giveUp, string error, CancellationToken cancellationToken)
    {
        var status = giveUp ? "FAILED" : "PENDING";
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications.outgoing_emails SET status = {status}, next_attempt_at = now() + {retryAfter}, last_error = {error}
            WHERE event_id = {email.EventId} AND recipient_id = {email.RecipientId} AND kind = {email.Kind} AND status = 'PENDING'
            """, cancellationToken);
    }

    /// <summary>Claimed, never started: due again at once, for another task, and the claim does not count as an attempt.</summary>
    public Task GiveBack(IReadOnlyCollection<ClaimedEmail> emails, CancellationToken cancellationToken)
    {
        var events = emails.Select(e => e.EventId).ToArray();
        var recipients = emails.Select(e => e.RecipientId).ToArray();
        var kinds = emails.Select(e => e.Kind).ToArray();
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications.outgoing_emails o SET attempts = o.attempts - 1, next_attempt_at = now()
            FROM unnest({events}, {recipients}, {kinds}) AS back(event_id, recipient_id, kind)
            WHERE o.event_id = back.event_id AND o.recipient_id = back.recipient_id AND o.kind = back.kind AND o.status = 'PENDING'
            """, cancellationToken);
    }

    private Task<int> Finish(ClaimedEmail email, string status, string? error, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications.outgoing_emails SET status = {status}, sent_at = CASE WHEN {status} = 'SENT' THEN now() END, last_error = {error}
            WHERE event_id = {email.EventId} AND recipient_id = {email.RecipientId} AND kind = {email.Kind} AND status = 'PENDING'
            """, cancellationToken);
}

/// <summary>
/// A user's address, from Notifications' own copy while it is fresh, otherwise from the user pool,
/// whose lookups have a low quota of their own. A retry, and every later email to the same person,
/// finds it here.
/// </summary>
internal sealed class AddressBook(NotificationsDbContext db, IUserDirectory directory, IOptions<NotificationsOptions> options)
{
    public async Task<string?> EmailOf(Guid userId, CancellationToken cancellationToken)
    {
        var freshFor = options.Value.AddressFreshFor;
        var kept = await db.Database.SqlQuery<string>($"""
            SELECT address AS "Value" FROM notifications.recipient_addresses WHERE user_id = {userId} AND looked_up_at > now() - {freshFor}
            """).ToListAsync(cancellationToken);
        if (kept.Count == 1)
        {
            return kept[0];
        }

        var address = await directory.EmailOf(userId, cancellationToken);
        if (address is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications.recipient_addresses (user_id, address, looked_up_at) VALUES ({userId}, {address}, now())
                ON CONFLICT (user_id) DO UPDATE SET address = EXCLUDED.address, looked_up_at = EXCLUDED.looked_up_at
                """, cancellationToken);
        }

        return address;
    }
}
