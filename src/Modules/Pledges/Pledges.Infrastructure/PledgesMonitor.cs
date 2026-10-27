using BuildingBlocks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Pledges.Application;
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
/// The nightly check of lesson 5, as Round 2 needs it (lesson s3-l05): pledges no longer write a
/// snapshot, so what a page shows is the aggregator's display total. Each campaign's display total is
/// compared with its ledger's sum; a difference is looked at again after a few seconds, longer than
/// the aggregator takes to refresh a changed campaign, and only one that persists is logged with its
/// campaign and counted. The count raises an alarm. It runs as the jobs role: summing every ledger
/// may take longer than the request path's two seconds.
/// </summary>
internal sealed partial class TotalsCheck(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<TotalsCheck> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured Mismatches = new Operations.Measured(
        "totals.display_mismatches", "{campaign}", "Campaigns whose display total differs from their ledger's sum, at the last check").Register();

    /// <summary>How long a difference must last to count: more than the aggregator's two seconds.</summary>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromSeconds(5);

    protected override TimeSpan Every => configuration.GetValue("Pledges:CheckTotalsEvery", TimeSpan.FromHours(24));

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PledgesDbContext>();
        services.GetRequiredService<PledgesJobsConnection>().Use(db);
        var store = services.GetRequiredService<ITotalsStore>();
        var campaigns = await db.FundingWindows.AsNoTracking().Select(w => w.CampaignId).ToListAsync(cancellationToken);

        var suspects = new List<Guid>();
        foreach (var campaignId in campaigns)
        {
            if (await Differs(db, store, campaignId, cancellationToken))
            {
                suspects.Add(campaignId);
            }
        }

        var different = new List<Guid>();
        if (suspects.Count > 0)
        {
            await Task.Delay(LookAgainAfter, cancellationToken);
            foreach (var campaignId in suspects)
            {
                if (await Differs(db, store, campaignId, cancellationToken))
                {
                    LogMismatch(logger, campaignId);
                    different.Add(campaignId);
                }
            }
        }

        Mismatches.Set(different.Count);
    }

    private static async Task<bool> Differs(PledgesDbContext db, ITotalsStore store, Guid campaignId, CancellationToken cancellationToken)
    {
        if (await store.Get(campaignId, cancellationToken) is not { } shown)
        {
            return false; // not computed yet: the page computes it from the ledger on its first read
        }

        var sum = await db.Ledger.Where(e => e.CampaignId == campaignId).SumAsync(e => (long?)e.DeltaMinor, cancellationToken) ?? 0;
        return shown.PledgedMinor != sum;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Campaign {CampaignId}: the display total differs from the ledger's sum")]
    private static partial void LogMismatch(ILogger logger, Guid campaignId);
}
