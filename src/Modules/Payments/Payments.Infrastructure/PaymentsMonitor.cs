using BuildingBlocks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure;

/// <summary>
/// Payments' failure mode, as a number: a charge stuck in CHARGING, its attempt IN_FLIGHT for more
/// than 10 minutes. The resolver should have settled it; the PSP may be down, or no worker running.
/// And two rules of lesson s1-a02 as queries that must return 0: a pledge is charged at most once,
/// and only a funded campaign's pledges are charged (a settlement job exists only after a FUNDED decision).
/// </summary>
internal sealed partial class PaymentsMonitor(IServiceScopeFactory scopes, ILogger<PaymentsMonitor> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured StuckCharging = new Operations.Measured(
        "charges.stuck_charging", "{charge}", "Charges CHARGING with an attempt in flight for more than 10 minutes").Register();

    public static readonly Operations.Measured DoubleCharges = new Operations.Measured(
        "invariants.double_charges", "{pledge}", "Pledges with more than one succeeded charge attempt").Register();

    public static readonly Operations.Measured ChargesWithoutSettlement = new Operations.Measured(
        "invariants.charges_without_settlement", "{charge}", "Charges of a campaign that has no settlement job, so no FUNDED decision").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PaymentsDbContext>();
        StuckCharging.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM payments.charges c
            JOIN payments.charge_attempts a ON a.pledge_id = c.pledge_id AND a.attempt_no = c.attempt_no
            WHERE c.status = 'CHARGING' AND a.status = 'IN_FLIGHT' AND a.created_at < now() - interval '10 minutes'
            """).SingleAsync(cancellationToken));
        DoubleCharges.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
                SELECT pledge_id FROM payments.charge_attempts WHERE status = 'SUCCEEDED' GROUP BY pledge_id HAVING count(*) > 1) twice
            """).SingleAsync(cancellationToken));
        ChargesWithoutSettlement.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM payments.charges c
            WHERE NOT EXISTS (SELECT 1 FROM payments.settlement_jobs j WHERE j.campaign_id = c.campaign_id)
            """).SingleAsync(cancellationToken));
    }
}
