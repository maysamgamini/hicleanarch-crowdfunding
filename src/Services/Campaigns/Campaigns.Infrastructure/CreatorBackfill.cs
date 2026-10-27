using BuildingBlocks.Application;

using Campaigns.Contracts;
using Campaigns.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Campaigns.Infrastructure;

/// <summary>
/// <c>record-creators</c>, run once after the deploy that makes Pledges store creators (lesson
/// s5-l05): a <see cref="CampaignCreatorRecorded"/> with a fresh event id for every LIVE campaign,
/// written to Campaigns' outbox in batches. Replaying <see cref="CampaignPublished"/> would not do:
/// Pledges' inbox drops an event id it has already handled. Running it again is harmless; Pledges
/// fills a creator only where none is stored.
/// </summary>
public static partial class CreatorBackfill
{
    public const int BatchSize = 500;

    public static async Task<int> RecordCreators(this IServiceProvider services, CancellationToken cancellationToken)
    {
        var after = Guid.Empty;
        var published = 0;
        while (true)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignsDbContext>();
            var rows = await db.Database.SqlQuery<LiveCreator>($"""
                SELECT id AS campaign_id, creator_id FROM campaigns.campaigns
                WHERE status = 'LIVE' AND id > {after} ORDER BY id LIMIT {BatchSize}
                """).ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            var outbox = scope.ServiceProvider.GetRequiredKeyedService<IOutbox>(CampaignsModuleName.Value);
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await outbox.AddRange([.. rows.Select(r => new CampaignCreatorRecorded(Guid.CreateVersion7(), now, r.CampaignId, r.CreatorId))], cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            published += rows.Count;
            after = rows[^1].CampaignId;
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CreatorBackfill));
        LogPublished(logger, published);
        return published;
    }

    private sealed record LiveCreator(Guid CampaignId, Guid CreatorId);

    [LoggerMessage(Level = LogLevel.Information, Message = "record-creators: published CampaignCreatorRecorded for {Campaigns} LIVE campaigns")]
    private static partial void LogPublished(ILogger logger, int campaigns);
}
