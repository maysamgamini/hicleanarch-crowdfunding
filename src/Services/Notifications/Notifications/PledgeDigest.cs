using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Notifications;

/// <summary>
/// The creator's daily digest of pledges, instead of an email per pledge: a hot campaign takes
/// thousands of pledges a minute, which as single emails would bury the creator's inbox and spend
/// as much of the SES quota as the backers' own confirmations. One email per campaign and UTC day,
/// with the pledges that counted and those cancelled that day, as Notifications heard them.
/// </summary>
internal sealed class PledgeDigests(NotificationsDbContext db)
{
    /// <summary>
    /// Records the digest of <paramref name="day"/> (a UTC date) for every campaign with pledges
    /// that day, in one statement. Its event id is made from the campaign and the day, so every task
    /// may run it, as often as it likes: a digest is recorded once.
    /// </summary>
    public Task<int> Record(DateOnly day, CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications.outgoing_emails (event_id, recipient_id, kind, subject, body, attempts)
            SELECT md5('pledge_digest:' || d.campaign_id::text || ':' || {date})::uuid, c.creator_id, 'pledge_digest',
                   'Your campaign''s pledges on ' || {date},
                   format('On %s (UTC) your campaign got %s new pledges, and %s pledges were cancelled.', {date}, d.added, d.cancelled), 0
            FROM (SELECT campaign_id,
                         count(*) FILTER (WHERE activated_at >= {from} AND activated_at < {to}) AS added,
                         count(*) FILTER (WHERE cancelled_at >= {from} AND cancelled_at < {to}) AS cancelled
                  FROM notifications.campaign_pledges
                  WHERE (activated_at >= {from} AND activated_at < {to}) OR (cancelled_at >= {from} AND cancelled_at < {to})
                  GROUP BY campaign_id) d
            JOIN notifications.campaign_creators c ON c.campaign_id = d.campaign_id
            WHERE NOT EXISTS (SELECT 1 FROM notifications.notification_preferences n
                              WHERE n.user_id = c.creator_id AND n.kind = 'pledge_digest' AND NOT n.enabled)
            ON CONFLICT DO NOTHING
            """, cancellationToken);
    }

    /// <summary>
    /// The last UTC day whose digest is due at <paramref name="now"/>: yesterday, once
    /// <paramref name="settle"/> has passed since midnight, so events still on their way are in it.
    /// </summary>
    public static DateOnly DueDay(DateTimeOffset now, TimeSpan settle) =>
        DateOnly.FromDateTime((now - settle).UtcDateTime).AddDays(-1);
}

/// <summary>Every <see cref="NotificationsOptions.DigestEvery"/>, on every task: records the digest due, if not yet recorded.</summary>
internal sealed partial class PledgeDigestJob(IServiceScopeFactory scopes, TimeProvider clock, IOptions<NotificationsOptions> options, ILogger<PledgeDigestJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.DigestEvery);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var day = PledgeDigests.DueDay(clock.GetUtcNow(), options.Value.DigestSettle);
                var recorded = await scope.ServiceProvider.GetRequiredService<PledgeDigests>().Record(day, stoppingToken);
                if (recorded > 0)
                {
                    LogRecorded(logger, recorded, day);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Count} pledge digests for {Day}")]
    private static partial void LogRecorded(ILogger logger, int count, DateOnly day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pledge digest pass failed; the next pass tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
