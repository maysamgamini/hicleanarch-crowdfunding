using BuildingBlocks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Pledges.Infrastructure.Persistence;

namespace Pledges.Infrastructure;

/// <summary>
/// The rules of lesson s1-a02 as queries that must return 0, measured every minute: where the
/// failure-mode gauges watch how a rule would break, these watch the rule itself. Each is an alarm
/// above 0. They run as the jobs role, since a scan may take longer than a request's two seconds.
/// </summary>
internal sealed class PledgesInvariants(IServiceScopeFactory scopes, ILogger<PledgesInvariants> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured OversoldTiers = new Operations.Measured(
        "invariants.oversold_tiers", "{tier}", "Limited tiers with more places taken than their limit, by their counter or by their live pledges").Register();

    public static readonly Operations.Measured ChargedUnfunded = new Operations.Measured(
        "invariants.charged_unfunded", "{pledge}", "Pledges marked for a charge, or charged, in a campaign that was not funded").Register();

    public static readonly Operations.Measured LedgerAfterDecision = new Operations.Measured(
        "invariants.ledger_after_decision", "{entry}", "Ledger entries, other than drops, written after the campaign's decision (campaigns decided in the last 30 days)").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PledgesDbContext>();
        services.GetRequiredService<PledgesJobsConnection>().Use(db);

        // A place is held from the pledge until its end: waiting for a card, active, and after the
        // deadline while it is charged.
        OversoldTiers.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pledges.tier_counters t
            WHERE t.limit_qty IS NOT NULL
              AND (t.claimed + t.held > t.limit_qty
                   OR (SELECT count(*) FROM pledges.pledges p
                       WHERE p.tier_id = t.tier_id AND p.status IN ('PENDING_SETUP', 'ACTIVE', 'TO_CHARGE', 'CHARGED')) > t.limit_qty)
            """).SingleAsync(cancellationToken));
        ChargedUnfunded.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pledges.pledges p
            JOIN pledges.funding_windows w ON w.campaign_id = p.campaign_id
            WHERE w.status IN ('UNSUCCESSFUL', 'CANCELLED') AND p.status IN ('TO_CHARGE', 'CHARGED')
            """).SingleAsync(cancellationToken));
        LedgerAfterDecision.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pledges.funding_windows w
            JOIN pledges.pledge_ledger e ON e.campaign_id = w.campaign_id AND e.created_at > w.decided_at
            WHERE w.decided_at > now() - interval '30 days' AND e.kind <> 'DROP'
            """).SingleAsync(cancellationToken));
    }
}
