using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;

using Pledges.Infrastructure.Persistence;

namespace IntegrationTests;

/// <summary>Lesson s3-l06: in the last 24 hours, no cancel or decrease takes a funded campaign below its goal.</summary>
public sealed class LastDayCancelTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    /// <summary>A campaign with a $100 goal and the given pledges, each from its own backer, with its deadline set.</summary>
    private async Task<(Scenario Given, Scenario.Published Campaign, List<(HttpClient Backer, string Pledge)> Pledges)> Campaign(
        string deadlineFromNow, params long[] amounts)
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(10_000);
        var pledges = new List<(HttpClient, string)>();
        foreach (var amount in amounts)
        {
            var backer = given.Backer(Guid.NewGuid());
            var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount), Ct);
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
            pledges.Add((backer, placed.Headers.Location!.ToString()));
        }

        await given.Execute($"UPDATE pledges.funding_windows SET deadline = now() + interval '{deadlineFromNow}' WHERE campaign_id = @id", campaign.CampaignId);
        return (given, campaign, pledges);
    }

    private static async Task<string?> Reason(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? null : (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString();

    [Fact]
    public async Task On_the_last_day_a_cancel_that_would_drop_a_funded_campaign_below_its_goal_is_refused()
    {
        var (given, campaign, pledges) = await Campaign("1 hour", 6_000, 4_100);

        var cancel = await pledges[0].Backer.DeleteAsync(pledges[0].Pledge, Ct);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("would_drop_below_goal", await Reason(cancel));
        Assert.Equal(10_100L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task On_the_last_day_a_decrease_below_the_goal_is_refused_and_one_that_stays_above_is_not()
    {
        var (given, campaign, pledges) = await Campaign("1 hour", 6_000, 4_100);
        var (backer, pledge) = pledges[0];

        var tooFar = await backer.PatchAsJsonAsync(pledge, new { amount_minor = 5_000, tier_id = (string?)null }, Ct);
        var little = await backer.PatchAsJsonAsync(pledge, new { amount_minor = 5_950, tier_id = (string?)null }, Ct);

        Assert.Equal("would_drop_below_goal", await Reason(tooFar));
        Assert.Equal(HttpStatusCode.OK, little.StatusCode);
        Assert.Equal(10_050L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Before_the_last_day_and_below_the_goal_cancels_are_free()
    {
        var (_, _, early) = await Campaign("3 days", 6_000, 4_100);
        Assert.Equal(HttpStatusCode.OK, (await early[0].Backer.DeleteAsync(early[0].Pledge, Ct)).StatusCode);

        // Already below the goal on the last day: the rule protects nothing, so the cancel goes through.
        var (_, _, short_) = await Campaign("1 hour", 3_000, 2_000);
        Assert.Equal(HttpStatusCode.OK, (await short_[0].Backer.DeleteAsync(short_[0].Pledge, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("23 hours 59 minutes", HttpStatusCode.Conflict)]
    [InlineData("24 hours 1 minute", HttpStatusCode.OK)]
    public async Task The_last_day_is_the_24_hours_before_the_deadline(string deadlineFromNow, HttpStatusCode answer)
    {
        var (_, _, pledges) = await Campaign(deadlineFromNow, 6_000, 4_100);

        Assert.Equal(answer, (await pledges[0].Backer.DeleteAsync(pledges[0].Pledge, Ct)).StatusCode);
    }

    [Fact]
    public async Task On_the_last_day_a_cancel_that_leaves_the_campaign_exactly_at_its_goal_is_allowed()
    {
        var (given, campaign, pledges) = await Campaign("1 hour", 100, 10_000);

        var cancel = await pledges[0].Backer.DeleteAsync(pledges[0].Pledge, Ct);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(10_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task Two_cancels_at_once_cannot_both_pass()
    {
        // $101 against a $100 goal: either 60-cent cancel alone keeps the goal, both together would not.
        for (var round = 0; round < 5; round++)
        {
            var (given, campaign, pledges) = await Campaign("1 hour", 60, 60, 9_980);

            var answers = await Task.WhenAll(pledges.Take(2).Select(p => CancelUntilAnswered(p.Backer, p.Pledge)));

            Assert.Equal(1, answers.Count(a => a.Status == HttpStatusCode.OK));
            Assert.Equal(1, answers.Count(a => a.Reason == "would_drop_below_goal"));
            Assert.Equal(10_040L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
        }
    }

    [Fact]
    public async Task A_cancel_that_cannot_get_its_turn_quickly_is_told_to_try_again()
    {
        var (given, campaign, pledges) = await Campaign("1 hour", 6_000, 4_100, 900);

        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var turn = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('pledges.last_day:' || @id::text, 0))", owner);
        turn.Parameters.AddWithValue("id", campaign.CampaignId);
        await turn.ExecuteNonQueryAsync(Ct);

        var watch = Stopwatch.StartNew();
        var busy = await pledges[2].Backer.DeleteAsync(pledges[2].Pledge, Ct);

        // The 300 ms wait for the turn answered, not the two-second limit, which would end the session.
        Assert.True(watch.Elapsed < TimeFence.Default.TransactionLimit, $"The busy answer took {watch.Elapsed}.");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("try_again", await Reason(busy));
        Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter?.Delta);
        Assert.Equal(11_000L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
    }

    [Fact]
    public async Task On_the_last_day_new_pledges_and_increases_do_not_wait_for_the_cancels_turn()
    {
        var (given, campaign, pledges) = await Campaign("1 hour", 6_000, 4_100);

        await using var owner = new NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var turn = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('pledges.last_day:' || @id::text, 0))", owner);
        turn.Parameters.AddWithValue("id", campaign.CampaignId);
        await turn.ExecuteNonQueryAsync(Ct);

        var placed = await given.Backer(Guid.NewGuid()).PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(500), Ct);
        var raised = await pledges[1].Backer.PatchAsJsonAsync(pledges[1].Pledge, new { amount_minor = 5_000, tier_id = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
    }

    /// <summary>A cancel, sent again with the same key while its turn is busy, until it gets a real answer.</summary>
    private static async Task<(HttpStatusCode Status, string? Reason)> CancelUntilAnswered(HttpClient backer, string pledge)
    {
        var key = Guid.NewGuid().ToString();
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, pledge);
            request.Headers.Add("Idempotency-Key", key);
            using var response = await backer.SendAsync(request, Ct);
            var reason = await Reason(response);
            if (reason != "try_again")
            {
                return (response.StatusCode, reason);
            }

            await Task.Delay(100, Ct);
        }
    }
}
