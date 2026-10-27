using Microsoft.EntityFrameworkCore;

namespace Notifications;

/// <summary>
/// What the handlers do: record who is to be told, never tell. Each email is one row in
/// <c>outgoing_emails</c>, written in the handler's own transaction with its inbox row, keyed by the
/// event, the recipient and the kind, so a repeated event records nothing twice. A fan-out to every
/// backer is one statement, however many backers there are. <see cref="EmailWorker"/> sends the rows,
/// paced, and retries each recipient on their own. Preferences are honoured when the row is recorded.
/// </summary>
internal sealed class Notifier(NotificationsDbContext db)
{
    public Task Tell(Guid eventId, Guid recipientId, string kind, string subject, string body, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.outgoing_emails (event_id, recipient_id, kind, subject, body, attempts)
            SELECT {eventId}, {recipientId}, {kind}, {subject}, {body}, 0
            WHERE NOT EXISTS (SELECT 1 FROM notifications.notification_preferences
                              WHERE user_id = {recipientId} AND kind = {kind} AND NOT enabled)
            ON CONFLICT DO NOTHING
            """, cancellationToken);

    /// <summary>One row per distinct backer of the campaign's live pledges, in one statement.</summary>
    public Task<int> TellBackers(Guid eventId, Guid campaignId, string kind, string subject, string body, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.outgoing_emails (event_id, recipient_id, kind, subject, body, attempts)
            SELECT DISTINCT {eventId}, p.backer_id, {kind}, {subject}, {body}, 0
            FROM notifications.campaign_pledges p
            WHERE p.campaign_id = {campaignId} AND p.activated_at IS NOT NULL AND p.cancelled_at IS NULL AND p.released_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM notifications.notification_preferences n
                              WHERE n.user_id = p.backer_id AND n.kind = {kind} AND NOT n.enabled)
            ON CONFLICT DO NOTHING
            """, cancellationToken);

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
}
