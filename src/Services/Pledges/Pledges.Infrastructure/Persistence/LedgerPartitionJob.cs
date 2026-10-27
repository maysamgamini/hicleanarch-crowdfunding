using System.Diagnostics.Metrics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pledges.Infrastructure.Persistence;

/// <summary>
/// Keeps the partitioned ledger healthy, at start and then once a day: makes sure the next months'
/// partitions exist and prunes replay guards older than 30 days in small batches. Partitions are
/// created by a function the owner wrote and the module's role may only execute, so the role still
/// cannot create tables. Detaching old months is not this job's: only the owner may, in the deploy
/// step (<see cref="PledgesMigrator"/>).
/// </summary>
internal sealed partial class LedgerPartitionJob(
    IServiceScopeFactory scopes,
    ILogger<LedgerPartitionJob> logger) : BackgroundService
{
    public const string MeterName = "CrowdFunding.Pledges";
    public const int MonthsAhead = 3;
    public const int CauseDays = 30;
    private const int PruneBatch = 10_000;

    private static readonly Meter Meter = new(MeterName);
    private int _monthsAhead = MonthsAhead;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Alarm when this falls below 1: next month's partition is missing (s2-l16 wires the alarm).
        Meter.CreateObservableGauge("ledger.partition_months_ahead", () => _monthsAhead, "{month}",
            "Whole months ahead of now that already have a ledger partition.");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnce(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e);
            }

            await Task.Delay(TimeSpan.FromDays(1), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    internal async Task RunOnce(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pledges.ensure_ledger_partitions(date_trunc('month', now() AT TIME ZONE 'UTC')::date, {MonthsAhead})",
            cancellationToken);
        _monthsAhead = await db.Database
            .SqlQuery<int>($"SELECT pledges.ledger_partition_months_ahead() AS \"Value\"")
            .SingleAsync(cancellationToken);

        int pruned;
        do
        {
            pruned = await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM pledges.ledger_causes WHERE ctid IN (
                    SELECT ctid FROM pledges.ledger_causes
                    WHERE created_at < now() - make_interval(days => {CauseDays}) LIMIT {PruneBatch})
                """, cancellationToken);
        }
        while (pruned == PruneBatch);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ledger partition maintenance failed; it runs again tomorrow")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
