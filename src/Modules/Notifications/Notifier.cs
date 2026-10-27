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

    /// <summary>Who backs what, per pledge: kept in the handler's own transaction, with its inbox row.</summary>
    public Task Activated(Guid pledgeId, Guid campaignId, Guid backerId, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_pledges (pledge_id, campaign_id, backer_id, activated_at) VALUES ({pledgeId}, {campaignId}, {backerId}, {at})
            ON CONFLICT (pledge_id) DO UPDATE SET activated_at = coalesce(campaign_pledges.activated_at, EXCLUDED.activated_at)
            """, cancellationToken);

    /// <summary>The backer cancelled the pledge. Heard before its activation, it leaves a row the activation cannot revive.</summary>
    public Task Cancelled(Guid pledgeId, Guid campaignId, Guid backerId, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_pledges (pledge_id, campaign_id, backer_id, cancelled_at) VALUES ({pledgeId}, {campaignId}, {backerId}, {at})
            ON CONFLICT (pledge_id) DO UPDATE SET cancelled_at = coalesce(campaign_pledges.cancelled_at, EXCLUDED.cancelled_at)
            """, cancellationToken);

    /// <summary>The campaign was cancelled and the pledge released.</summary>
    public Task Released(Guid pledgeId, Guid campaignId, Guid backerId, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_pledges (pledge_id, campaign_id, backer_id, released_at) VALUES ({pledgeId}, {campaignId}, {backerId}, {at})
            ON CONFLICT (pledge_id) DO UPDATE SET released_at = coalesce(campaign_pledges.released_at, EXCLUDED.released_at)
            """, cancellationToken);

    public Task RememberCreator(Guid campaignId, Guid creatorId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.campaign_creators (campaign_id, creator_id) VALUES ({campaignId}, {creatorId}) ON CONFLICT DO NOTHING
            """, cancellationToken);

    public async Task<Guid?> CreatorOf(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Set<CampaignCreator>().AsNoTracking().Where(c => c.CampaignId == campaignId).Select(c => (Guid?)c.CreatorId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>The distinct backers of the campaign's live pledges: a backer who cancelled and pledged again is one of them.</summary>
    public async Task<IReadOnlyList<Guid>> BackersOf(Guid campaignId, CancellationToken cancellationToken) =>
        await db.Set<CampaignPledge>().AsNoTracking()
            .Where(p => p.CampaignId == campaignId && p.ActivatedAt != null && p.CancelledAt == null && p.ReleasedAt == null)
            .Select(p => p.BackerId).Distinct().ToListAsync(cancellationToken);
}
