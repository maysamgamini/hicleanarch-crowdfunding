using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Pledges.Application;
using Pledges.Domain;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>The ledger partitioned by month (lesson s3-l10): where entries land, what still guards them, what a sum reads.</summary>
public sealed class LedgerPartitionTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime ThisMonth = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SevenMonthsAgo = ThisMonth.AddMonths(-7);
    private static readonly DateTime TwoMonthsAgo = ThisMonth.AddMonths(-2);

    public void Dispose() => _host.Dispose();

    private static string PartitionOf(DateTime month) => $"pledge_ledger_{month:yyyy_MM}";

    [Fact]
    public async Task An_entry_lands_in_the_partition_of_its_month()
    {
        await AsOwner("SELECT pledges.ensure_ledger_partitions(@from, 3)", ("from", DateOnly.FromDateTime(SevenMonthsAgo)));
        var old = $"old-{Guid.NewGuid():N}";
        await AsOwner("""
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind, created_at)
            VALUES (gen_random_uuid(), gen_random_uuid(), @cause, 100, 'ACTIVATE', @at)
            """, ("cause", old), ("at", SevenMonthsAgo.AddDays(10)));

        var pledge = NewPledge();
        await Append(LedgerEntry.Activate(pledge, $"now-{Guid.NewGuid():N}"));

        Assert.EndsWith(PartitionOf(SevenMonthsAgo), await PartitionHolding("cause_id = @v", old));
        Assert.EndsWith(PartitionOf(ThisMonth), await PartitionHolding("pledge_id = @v", pledge.Id));
    }

    [Fact]
    public async Task A_replayed_cause_is_refused_whatever_month_its_entry_is_in()
    {
        await AsOwner("SELECT pledges.ensure_ledger_partitions(@from, 3)", ("from", DateOnly.FromDateTime(TwoMonthsAgo)));
        var cause = $"replayed-{Guid.NewGuid():N}";
        await AsOwner("""
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind, created_at)
            VALUES (gen_random_uuid(), gen_random_uuid(), @cause, 100, 'ACTIVATE', @at);
            """, ("cause", cause), ("at", TwoMonthsAgo.AddDays(3)));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => Append(LedgerEntry.Activate(NewPledge(), cause)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
        Assert.Equal("pk_ledger_causes", refused.ConstraintName);
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.pledge_ledger WHERE cause_id = @v", cause));
    }

    [Fact]
    public async Task One_pledge_cannot_be_activated_twice_by_two_causes()
    {
        var pledge = NewPledge();
        await Append(LedgerEntry.Activate(pledge, $"first-{Guid.NewGuid():N}"));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => Append(LedgerEntry.Activate(pledge, $"second-{Guid.NewGuid():N}")));

        Assert.Equal("pk_pledge_activations", refused.ConstraintName);
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.pledge_ledger WHERE pledge_id = @v", pledge.Id));
    }

    [Fact]
    public async Task An_entry_written_straight_into_the_ledger_still_gets_its_guards()
    {
        // As an older task would during a rolling deploy: no guard rows written by the application.
        await using var pledges = new NpgsqlConnection(database.Pledges);
        await pledges.OpenAsync(Ct);
        var cause = $"straight-{Guid.NewGuid():N}";
        var insert = $"""
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind)
            VALUES (gen_random_uuid(), gen_random_uuid(), '{cause}', 100, 'ACTIVATE')
            """;
        await Run(pledges, insert);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => Run(pledges, insert));

        Assert.Equal("pk_ledger_causes", refused.ConstraintName);
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.ledger_causes WHERE cause_id = @v", cause));
    }

    [Fact]
    public async Task Only_old_months_of_finished_campaigns_are_detached()
    {
        var given = new Scenario(database, _host);
        var live = await given.Publish();
        var failed = await given.Publish();
        var thirtyMonthsAgo = ThisMonth.AddMonths(-30);
        var twentyMonthsAgo = ThisMonth.AddMonths(-20);
        await AsOwner("SELECT pledges.ensure_ledger_partitions(@from, 3)", ("from", DateOnly.FromDateTime(thirtyMonthsAgo)));
        await AsOwner("""
            UPDATE pledges.funding_windows SET status = 'UNSUCCESSFUL' WHERE campaign_id = @failed;
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind, created_at) VALUES
                (@failed, gen_random_uuid(), gen_random_uuid()::text, 500, 'ACTIVATE', @thirty),
                (@live, gen_random_uuid(), gen_random_uuid()::text, 500, 'ACTIVATE', @twenty);
            """, ("failed", failed.CampaignId), ("live", live.CampaignId), ("thirty", thirtyMonthsAgo.AddDays(2)), ("twenty", twentyMonthsAgo.AddDays(2)));

        // Never a recent month, and never a month that holds a campaign still in play.
        var recent = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwner("SELECT pledges.detach_ledger_partitions_before(@m)", ("m", DateOnly.FromDateTime(ThisMonth.AddMonths(-12)))));
        var inPlay = await Assert.ThrowsAsync<PostgresException>(() =>
            AsOwner("SELECT pledges.detach_ledger_partitions_before(@m)", ("m", DateOnly.FromDateTime(ThisMonth.AddMonths(-14)))));
        Assert.Equal(PostgresErrorCodes.CheckViolation, recent.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, inPlay.SqlState);
        Assert.Contains(PartitionOf(twentyMonthsAgo), inPlay.MessageText, StringComparison.Ordinal);
        Assert.NotNull(await Scalar($"SELECT to_regclass('pledges.{PartitionOf(thirtyMonthsAgo)}')::text"));
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @v", failed.CampaignId));

        // Months before the live campaign's, with only a finished campaign in them, can go.
        var detached = (int)(await Scalar("SELECT pledges.detach_ledger_partitions_before(@m)", ("m", DateOnly.FromDateTime(twentyMonthsAgo))))!;

        Assert.True(detached >= 1);
        Assert.Equal(0L, await Count("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @v", failed.CampaignId));
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @v", live.CampaignId));
    }

    [Fact]
    public async Task A_sum_from_the_opening_reads_only_the_months_since_then()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await AsOwner("SELECT pledges.ensure_ledger_partitions(@from, 3)", ("from", DateOnly.FromDateTime(SevenMonthsAgo)));
        await AsOwner("""
            UPDATE pledges.funding_windows SET opened_at = @opened WHERE campaign_id = @campaign;
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind, created_at) VALUES
                (@campaign, gen_random_uuid(), gen_random_uuid()::text, 500, 'ACTIVATE', @before),
                (@campaign, gen_random_uuid(), gen_random_uuid()::text, 700, 'ACTIVATE', @after),
                (@campaign, gen_random_uuid(), gen_random_uuid()::text, 300, 'ACTIVATE', now());
            """, ("campaign", campaign.CampaignId), ("opened", TwoMonthsAgo), ("before", SevenMonthsAgo.AddDays(1)), ("after", TwoMonthsAgo.AddDays(1)));

        var sum = await _host.Services.GetRequiredService<ILedgerSums>().SumSinceOpened(campaign.CampaignId, Ct);
        var plan = await Plan("""
            EXPLAIN (COSTS OFF) SELECT sum(delta_minor) FROM pledges.pledge_ledger
            WHERE campaign_id = @campaign AND created_at >= @opened
            """, ("campaign", campaign.CampaignId), ("opened", TwoMonthsAgo));

        Assert.Equal(1_000L, sum);
        Assert.Contains(PartitionOf(TwoMonthsAgo), plan, StringComparison.Ordinal);
        Assert.Contains(PartitionOf(ThisMonth), plan, StringComparison.Ordinal);
        Assert.DoesNotContain(PartitionOf(SevenMonthsAgo), plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_module_role_appends_through_the_ledger_and_can_touch_nothing_else()
    {
        await using var pledges = new NpgsqlConnection(database.Pledges);
        await pledges.OpenAsync(Ct);

        // Through the partitioned table: adding is allowed.
        await Run(pledges, """
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind)
            VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid()::text, 1, 'ACTIVATE')
            """);
        // A partition the role made itself, through the owner's function, gives it no rights either.
        await Run(pledges, "SELECT pledges.ensure_ledger_partitions(date_trunc('month', now())::date, 4)");
        var future = PartitionOf(ThisMonth.AddMonths(4));

        foreach (var sql in new[]
        {
            "UPDATE pledges.pledge_ledger SET delta_minor = 2",
            "DELETE FROM pledges.pledge_ledger",
            "TRUNCATE pledges.pledge_ledger",
            $"DELETE FROM pledges.{PartitionOf(ThisMonth)}",
            $"SELECT count(*) FROM pledges.{PartitionOf(ThisMonth)}",
            $"INSERT INTO pledges.{future} (campaign_id, pledge_id, cause_id, delta_minor, kind) VALUES (gen_random_uuid(), gen_random_uuid(), 'x', 1, 'ACTIVATE')",
            "DELETE FROM pledges.pledge_activations",
            "SELECT pledges.detach_ledger_partitions_before('2000-01-01')",
            "CREATE TABLE pledges.pledge_ledger_sneaky PARTITION OF pledges.pledge_ledger FOR VALUES FROM ('2099-01-01') TO ('2099-02-01')",
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => Run(pledges, sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
    }

    [Fact]
    public async Task The_daily_job_keeps_months_ahead_and_prunes_old_causes()
    {
        var old = $"old-cause-{Guid.NewGuid():N}";
        var recent = $"recent-cause-{Guid.NewGuid():N}";
        await AsOwner("""
            INSERT INTO pledges.ledger_causes (cause_id, kind, created_at) VALUES
                (@old, 'ACTIVATE', now() - interval '40 days'),
                (@recent, 'ACTIVATE', now() - interval '20 days');
            """, ("old", old), ("recent", recent));

        var job = _host.Services.GetServices<IHostedService>().OfType<LedgerPartitionJob>().Single();
        await job.RunOnce(Ct);

        Assert.True((int)(await Scalar("SELECT pledges.ledger_partition_months_ahead()"))! >= LedgerPartitionJob.MonthsAhead);
        Assert.Equal(0L, await Count("SELECT count(*) FROM pledges.ledger_causes WHERE cause_id = @v", old));
        Assert.Equal(1L, await Count("SELECT count(*) FROM pledges.ledger_causes WHERE cause_id = @v", recent));
    }

    private static Pledge NewPledge() => Pledge.WithSavedCard(Guid.NewGuid(), Guid.NewGuid(), null, 6_000, "pm_card_visa").Value;

    /// <summary>Appends through the module's own port and role, in one transaction, as a request would.</summary>
    private async Task Append(LedgerEntry entry)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IPledgeLedger>().Append(entry, Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task<string> PartitionHolding(string where, object value) =>
        (string)(await Scalar($"SELECT tableoid::regclass::text FROM pledges.pledge_ledger WHERE {where}", ("v", value)))!;

    private async Task<long> Count(string sql, object value) => Convert.ToInt64(await Scalar(sql, ("v", value)), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> Plan(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = await Open();
        await using var command = Command(owner, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private async Task<object?> Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = await Open();
        await using var command = Command(owner, sql, parameters);
        return await command.ExecuteScalarAsync(Ct);
    }

    private async Task AsOwner(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = await Open();
        await using var command = Command(owner, sql, parameters);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task Run(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<NpgsqlConnection> Open()
    {
        var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        return owner;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
