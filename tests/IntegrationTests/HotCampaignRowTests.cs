using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using BuildingBlocks.Application;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Pledges.Application;
using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>The hot campaign row, Round 2 (lesson s3-l05): insert-only pledges, a time fence, recomputed totals.</summary>
public sealed class HotCampaignRowTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_request_paths_role_is_ended_after_two_seconds_and_the_jobs_role_is_not()
    {
        await using var capped = new NpgsqlConnection(database.Pledges);
        await capped.OpenAsync(Ct);
        await using var transaction = await capped.BeginTransactionAsync(Ct);
        await using var sleep = new NpgsqlCommand("SELECT pg_sleep(4)", capped, transaction);
        var watch = Stopwatch.StartNew();

        var ended = await Assert.ThrowsAnyAsync<NpgsqlException>(() => sleep.ExecuteNonQueryAsync(Ct));

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.8), TimeSpan.FromSeconds(3.5));
        if (ended is PostgresException postgres)
        {
            Assert.Equal("25P04", postgres.SqlState); // transaction_timeout
        }

        await using var jobs = new NpgsqlConnection(database.PledgesJobs);
        await jobs.OpenAsync(Ct);
        await using var jobsTransaction = await jobs.BeginTransactionAsync(Ct);
        await using var longer = new NpgsqlCommand("SELECT pg_sleep(2.5)", jobs, jobsTransaction);
        await longer.ExecuteNonQueryAsync(Ct);
        await jobsTransaction.CommitAsync(Ct);
    }

    [Theory]
    [InlineData("SET LOCAL transaction_timeout = 0")]
    [InlineData("SET transaction_timeout = 0")]
    [InlineData("SET LOCAL transaction_timeout = '1min'")]
    public async Task A_pledge_transaction_without_the_two_second_limit_is_not_let_in(string lowering)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync(lowering, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IFundingWindows>().TryAccept(campaign.CampaignId, 6_000, 1, Ct));
    }

    [Fact]
    public async Task A_pledge_transaction_with_the_two_second_limit_is_let_in()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);

        Assert.True(await scope.ServiceProvider.GetRequiredService<IFundingWindows>().TryAccept(campaign.CampaignId, 6_000, 1, Ct));
    }

    [Fact]
    public async Task A_pledge_does_not_write_the_funding_window()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        const string Version = "SELECT xmin::text::bigint FROM pledges.funding_windows WHERE campaign_id = @id";
        var before = await given.Scalar<long>(Version, campaign.CampaignId);

        (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(6_000), Ct)).EnsureSuccessStatusCode();

        Assert.Equal(before, await given.Scalar<long>(Version, campaign.CampaignId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_pledge_that_saw_LIVE_and_committed_in_time_is_in_the_sum()
    {
        var (given, campaign, transaction) = await PledgeInFlightAcrossTheDeadline();

        var closing = CloseUntilDecided(campaign.CampaignId);
        await Scenario.Eventually(async () => await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId) == "ENDED");

        // The close has ended the campaign; half a second later, by the database's clock, it still
        // has not summed, because a transaction that read LIVE may still be open.
        await Scenario.Eventually(async () => await given.Scalar<bool>(
            "SELECT final_minor IS NOT NULL OR clock_timestamp() >= ended_at + interval '500 milliseconds' FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal("ENDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        await transaction.CommitAsync(Ct); // well inside the two-second limit
        await closing;

        Assert.Equal(6_000L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_pledge_that_saw_LIVE_and_ran_past_the_limit_is_ended_and_not_in_the_sum()
    {
        var (given, campaign, transaction) = await PledgeInFlightAcrossTheDeadline();

        await CloseUntilDecided(campaign.CampaignId);

        Assert.Equal(0L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        await Assert.ThrowsAnyAsync<Exception>(() => transaction.CommitAsync(Ct));
        Assert.Equal(0L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task A_decision_sooner_than_the_fences_wait_after_the_close_changes_nothing()
    {
        // A campaign another task has just ended, decided by a pass that did not wait: a stranded
        // campaign's next pass, or a second pass on another task.
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET status = 'ENDED', deadline = now() - interval '1 second', ended_at = clock_timestamp() WHERE campaign_id = @id", campaign.CampaignId);
        await using var scope = _host.Services.CreateAsyncScope();
        var closing = scope.ServiceProvider.GetRequiredService<IClosing>();

        Assert.False(await closing.TryDecide(campaign.CampaignId, funded: false, finalMinor: 0, Ct));
        Assert.Equal("ENDED", await given.Scalar<string>("SELECT status FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.True(await given.Scalar<bool>("SELECT final_minor IS NULL AND decided_at IS NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        // Once the wait is behind it, the same decision is taken.
        await given.Execute("UPDATE pledges.funding_windows SET ended_at = clock_timestamp() - interval '4 seconds' WHERE campaign_id = @id", campaign.CampaignId);
        Assert.True(await closing.TryDecide(campaign.CampaignId, funded: false, finalMinor: 0, Ct));
    }

    [Fact]
    public async Task The_aggregators_total_equals_the_ledger_sum()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        foreach (var amount in new long[] { 6_000, 9_900, 4_900 })
        {
            (await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount), Ct)).EnsureSuccessStatusCode();
        }

        var store = _host.Services.GetRequiredService<ITotalsStore>();
        await Scenario.Eventually(async () => (await store.Get(campaign.CampaignId, Ct))?.PledgedMinor == 20_800);

        var ledger = await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId);
        var stored = await store.Get(campaign.CampaignId, Ct);
        Assert.Equal(ledger, stored!.PledgedMinor);
        Assert.Equal(3, stored.Backers);

        var page = await _host.CreateClient().GetFromJsonAsync<JsonElement>($"/v1/campaigns/{campaign.Id}", Ct);
        Assert.Equal(20_800, page.GetProperty("pledged_minor").GetInt64());
    }

    [Fact]
    public async Task A_decision_whose_sum_changes_a_minute_later_is_found_by_the_recheck()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() WHERE campaign_id = @id", campaign.CampaignId);
        await CloseUntilDecided(campaign.CampaignId);

        // The decision is now two minutes old, and an entry that began before it committed after its
        // sum, as a failed fence would leave it. (The window opened earlier still: no entry precedes it.)
        await given.Execute($"""
            UPDATE pledges.funding_windows SET decided_at = decided_at - interval '2 minutes', opened_at = opened_at - interval '3 minutes' WHERE campaign_id = @id;
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind, created_at)
            SELECT campaign_id, '{Guid.NewGuid()}', 'test:{Guid.NewGuid():N}', 6000, 'ACTIVATE', decided_at - interval '1 second'
            FROM pledges.funding_windows WHERE campaign_id = @id;
            """, campaign.CampaignId);

        await Scenario.Eventually(async () =>
        {
            await using var scope = _host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IClosing>().RecheckDecided(50, Ct);
            return await given.Scalar<bool>("SELECT recheck_minor IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId);
        });

        Assert.Equal(0L, await given.Scalar<long>("SELECT final_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT recheck_minor FROM pledges.funding_windows WHERE campaign_id = @id", campaign.CampaignId));

        // The alarm's gauge is measured from the rows, by any task: a new host measures as it starts.
        using var restarted = database.StartHost();
        _ = restarted.Server;
        await Scenario.Eventually(() => Task.FromResult(Pledges.Infrastructure.PledgesMonitor.RecheckMismatches.Value >= 1));
    }

    [Fact]
    public async Task A_pledge_that_waits_too_long_for_a_tier_row_is_told_to_try_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 10));
        var tierId = Guid.ParseExact(campaign.TierIds[0]["tir_".Length..], "N");

        // Someone else holds the tier row.
        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var holding = await owner.BeginTransactionAsync(Ct);
        await using var lockRow = new NpgsqlCommand("SELECT 1 FROM pledges.tier_counters WHERE tier_id = @id FOR UPDATE", owner, holding);
        lockRow.Parameters.AddWithValue("id", tierId);
        await lockRow.ExecuteNonQueryAsync(Ct);

        var backer = given.Backer(Guid.NewGuid());
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
        {
            Content = JsonContent.Create(Scenario.Pledge(4_900, campaign.TierIds[0])),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var key = request.Headers.GetValues("Idempotency-Key").Single();
        var watch = Stopwatch.StartNew();
        var busy = await backer.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5));
        Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
        Assert.Equal("tier_busy", (await busy.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString());

        // Nothing was kept, so the same key works once the row is free.
        await holding.RollbackAsync(Ct);
        using var again = new HttpRequestMessage(HttpMethod.Post, $"/v1/campaigns/{campaign.Id}/pledges")
        {
            Content = JsonContent.Create(Scenario.Pledge(4_900, campaign.TierIds[0])),
        };
        again.Headers.Add("Idempotency-Key", key);
        Assert.Equal(HttpStatusCode.Created, (await backer.SendAsync(again, Ct)).StatusCode);
    }

    /// <summary>
    /// A campaign whose deadline passes while a pledge transaction is open: the transaction has read
    /// LIVE and added a ledger entry, and has not committed. The deadline is moved to the database's
    /// now() only after that read, so the transaction began just before it, with no sleep to outlast.
    /// </summary>
    private async Task<(Scenario Given, Scenario.Published Campaign, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction)> PledgeInFlightAcrossTheDeadline()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PledgesDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(Ct);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IFundingWindows>().TryAccept(campaign.CampaignId, 6_000, 1, Ct));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pledges.pledge_ledger (campaign_id, pledge_id, cause_id, delta_minor, kind)
            VALUES ({campaign.CampaignId}, {Guid.NewGuid()}, {"test:" + Guid.NewGuid().ToString("N")}, 6000, 'ACTIVATE')
            """, Ct);
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() WHERE campaign_id = @id", campaign.CampaignId);
        return (given, campaign, transaction);
    }

    /// <summary>
    /// Runs the close job's pass for this campaign only, as the jobs role, until it is decided. An
    /// untargeted pass would decide other tests' campaigns in this host, whose events this host then
    /// dispatches, not theirs.
    /// </summary>
    private async Task CloseUntilDecided(Guid campaignId)
    {
        var jobs = _host.Services.GetRequiredService<PledgesJobsConnection>();
        for (var i = 0; i < 20; i++)
        {
            await using (var scope = _host.Services.CreateAsyncScope())
            {
                jobs.Use(scope.ServiceProvider.GetRequiredService<PledgesDbContext>());
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send<CloseDueCampaigns, int>(new CloseDueCampaigns(campaignId), Ct);
            }

            await using var owner = new NpgsqlConnection(database.Owner);
            await owner.OpenAsync(Ct);
            await using var decided = new NpgsqlCommand("SELECT final_minor IS NOT NULL FROM pledges.funding_windows WHERE campaign_id = @id", owner);
            decided.Parameters.AddWithValue("id", campaignId);
            if ((bool)(await decided.ExecuteScalarAsync(Ct))!)
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        Assert.Fail("The campaign was not decided.");
    }
}
