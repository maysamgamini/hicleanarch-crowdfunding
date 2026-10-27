using Microsoft.EntityFrameworkCore;

namespace Notifications;

/// <summary>
/// Sends at most once per event and recipient, and honours the recipient's preferences. The row in
/// <c>sent_notifications</c> is written in the handler's transaction, before the email leaves; if the
/// send fails, the transaction rolls back and the event is delivered again.
/// </summary>
internal sealed class Notifier(NotificationsDbContext db, IEmailSender sender)
{
    public async Task Tell(Guid eventId, Guid recipientId, string kind, string subject, string body, CancellationToken cancellationToken)
    {
        var disabled = await db.Set<NotificationPreference>().AsNoTracking()
            .AnyAsync(p => p.UserId == recipientId && p.Kind == kind && !p.Enabled, cancellationToken);
        if (disabled)
        {
            return;
        }

        var first = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.sent_notifications (event_id, recipient_id, kind) VALUES ({eventId}, {recipientId}, {kind})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        if (first == 1)
        {
            await sender.Send(new Email(recipientId, subject, body), cancellationToken);
        }
    }

    public Task Remember(Guid campaignId, Guid backerId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_backers (campaign_id, backer_id) VALUES ({campaignId}, {backerId}) ON CONFLICT DO NOTHING
            """, cancellationToken);

    public Task Forget(Guid campaignId, Guid backerId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM notifications.campaign_backers WHERE campaign_id = {campaignId} AND backer_id = {backerId}
            """, cancellationToken);

    public async Task<IReadOnlyList<Guid>> BackersOf(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Set<CampaignBacker>().AsNoTracking().Where(b => b.CampaignId == campaignId).Select(b => b.BackerId).ToListAsync(cancellationToken);
}
