using BuildingBlocks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure;

/// <summary>
/// Pledges' failure modes, as numbers: a campaign past its deadline still LIVE (the close job is not
/// running), or ENDED for too long (a pass died between the close and the decision).
/// </summary>
internal sealed partial class PledgesMonitor(IServiceScopeFactory scopes, ILogger<PledgesMonitor> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured LivePastDeadline = new Operations.Measured(
        "campaigns.live_past_deadline", "{campaign}", "Campaigns more than 5 minutes past their deadline and still LIVE").Register();

    public static readonly Operations.Measured EndedUndecided = new Operations.Measured(
        "campaigns.ended_undecided", "{campaign}", "Campaigns ENDED for more than 5 minutes without a decision").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PledgesDbContext>();
        LivePastDeadline.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pledges.funding_windows WHERE status = 'LIVE' AND deadline < now() - interval '5 minutes'
            """).SingleAsync(cancellationToken));
        EndedUndecided.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pledges.funding_windows WHERE status = 'ENDED' AND ended_at < now() - interval '5 minutes'
            """).SingleAsync(cancellationToken));
    }
}

/// <summary>
/// Lesson 5's nightly check: every funding window's snapshot total against its ledger's sum. Any
/// difference is logged with its campaign and counted, and the count raises an alarm.
/// </summary>
internal sealed partial class SnapshotCheck(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SnapshotCheck> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured Mismatches = new Operations.Measured(
        "ledger.snapshot_mismatches", "{campaign}", "Funding windows whose snapshot total differs from their ledger's sum, at the last check").Register();

    protected override TimeSpan Every => configuration.GetValue("Pledges:CheckSnapshotsEvery", TimeSpan.FromHours(24));

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PledgesDbContext>();
        var different = await db.Database.SqlQuery<Guid>($"""
            SELECT w.campaign_id AS "Value" FROM pledges.funding_windows w
            WHERE w.pledged_minor <> (SELECT coalesce(sum(l.delta_minor), 0) FROM pledges.pledge_ledger l WHERE l.campaign_id = w.campaign_id)
            """).ToListAsync(cancellationToken);
        foreach (var campaignId in different)
        {
            LogMismatch(logger, campaignId);
        }

        Mismatches.Set(different.Count);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Campaign {CampaignId}: the snapshot total differs from the ledger's sum")]
    private static partial void LogMismatch(ILogger logger, Guid campaignId);
}
