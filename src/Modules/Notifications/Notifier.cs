using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications;

/// <summary>
/// Sends at most once per event and recipient, and honours the recipient's preferences. Each
/// recipient gets a transaction of their own, apart from the handler's: the row in
/// <c>sent_notifications</c> commits before the email leaves, so a failure for one recipient never
/// undoes, or repeats, the emails already sent to the others. If the send fails, the row is removed
/// and the error goes up, so the event is delivered again and only the recipients without a row are
/// told. A task that dies between the commit and the send loses that one email: at most once.
/// </summary>
internal sealed class Notifier(NotificationsDbContext db, IServiceScopeFactory scopes, IEmailSender sender)
{
    public async Task Tell(Guid eventId, Guid recipientId, string kind, string subject, string body, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(); // its own connection, outside the handler's transaction
        var disabled = await own.Set<NotificationPreference>().AsNoTracking()
            .AnyAsync(p => p.UserId == recipientId && p.Kind == kind && !p.Enabled, cancellationToken);
        if (disabled)
        {
            return;
        }

        var first = await own.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.sent_notifications (event_id, recipient_id, kind) VALUES ({eventId}, {recipientId}, {kind})
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        if (first == 0)
        {
            return; // told already
        }

        try
        {
            await sender.Send(new Email(recipientId, subject, body), cancellationToken);
        }
        catch
        {
            await own.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM notifications.sent_notifications WHERE event_id = {eventId} AND recipient_id = {recipientId} AND kind = {kind}
                """, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Who backs what: kept in the handler's own transaction, with its inbox row.</summary>
    public Task Remember(Guid campaignId, Guid backerId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_backers (campaign_id, backer_id) VALUES ({campaignId}, {backerId}) ON CONFLICT DO NOTHING
            """, cancellationToken);

    public Task RememberCreator(Guid campaignId, Guid creatorId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_creators (campaign_id, creator_id) VALUES ({campaignId}, {creatorId}) ON CONFLICT DO NOTHING
            """, cancellationToken);

    public async Task<Guid?> CreatorOf(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Set<CampaignCreator>().AsNoTracking().Where(c => c.CampaignId == campaignId).Select(c => (Guid?)c.CreatorId).SingleOrDefaultAsync(cancellationToken);

    public Task Forget(Guid campaignId, Guid backerId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM notifications.campaign_backers WHERE campaign_id = {campaignId} AND backer_id = {backerId}
            """, cancellationToken);

    public async Task<IReadOnlyList<Guid>> BackersOf(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Set<CampaignBacker>().AsNoTracking().Where(b => b.CampaignId == campaignId).Select(b => b.BackerId).ToListAsync(cancellationToken);
}
