using BuildingBlocks.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure;

/// <summary>
/// Payments' failure mode, as a number: a charge stuck in CHARGING, its attempt IN_FLIGHT for more
/// than 10 minutes. The resolver should have settled it; the PSP may be down, or no worker running.
/// </summary>
internal sealed partial class PaymentsMonitor(IServiceScopeFactory scopes, ILogger<PaymentsMonitor> logger) : ModuleMonitor(scopes, logger)
{
    public static readonly Operations.Measured StuckCharging = new Operations.Measured(
        "charges.stuck_charging", "{charge}", "Charges CHARGING with an attempt in flight for more than 10 minutes").Register();

    protected override async Task Measure(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<PaymentsDbContext>();
        StuckCharging.Set(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM payments.charges c
            JOIN payments.charge_attempts a ON a.pledge_id = c.pledge_id AND a.attempt_no = c.attempt_no
            WHERE c.status = 'CHARGING' AND a.status = 'IN_FLIGHT' AND a.created_at < now() - interval '10 minutes'
            """).SingleAsync(cancellationToken));
    }
}
