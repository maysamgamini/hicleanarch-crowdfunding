using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IntegrationTests;

/// <summary>Changes and cancels keep the total exact, and nothing moves after the deadline.</summary>
public sealed class ChangeAndCancelTests(Database database) : IDisposable
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host = database.StartHost();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static async Task<string> Place(HttpClient backer, Scenario.Published campaign, long amount, string? tier = null)
    {
        var placed = await backer.PostAsJsonAsync($"/v1/campaigns/{campaign.Id}/pledges", Scenario.Pledge(amount, tier), Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        return placed.Headers.Location!.ToString();
    }

    private static Task<HttpResponseMessage> Change(HttpClient backer, string pledge, long amount, string? tier = null) =>
        backer.PatchAsJsonAsync(pledge, new { amount_minor = amount, tier_id = tier }, Ct);

    /// <summary>
    /// A change sent again, with the same key, while the API says it is busy, as a client does. Ten
    /// changes queue on one row, so on a loaded machine some outlast the module role's 2 s
    /// transaction limit and are told to try again.
    /// </summary>
    private static Task<HttpResponseMessage> ChangeUntilAnswered(HttpClient backer, string pledge, long amount) =>
        Scenario.SendUntilAnswered(backer, () => new HttpRequestMessage(HttpMethod.Patch, pledge)
        {
            Content = JsonContent.Create(new { amount_minor = amount, tier_id = (string?)null }),
        });

    private static async Task<string> Reason(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("reason").GetString()!;

    // Round 2 (lesson s3-l05): the ledger is the total, and every live pledge is in it.
    private static async Task AssertTotalIsTheLedger(Scenario given, Guid campaignId)
    {
        var ledger = await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaignId);
        var active = await given.Scalar<long>("SELECT coalesce(sum(amount_minor), 0)::bigint FROM pledges.pledges WHERE campaign_id = @id AND status = 'ACTIVE'", campaignId);
        Assert.Equal(active, ledger);
    }

    /// <summary>
    /// A change that moves tiers waits only a moment for the tier rows; every statement after them waits
    /// for its locks as usual. Here the ledger is locked for 0.8 s, longer than the tier wait and well
    /// inside the request's time limit: the change waits and is made.
    /// </summary>
    [Fact]
    public async Task A_change_into_another_tier_waits_as_usual_for_a_lock_after_the_tier_rows()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 2), ("Founder", 9_900, null));
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 10_000, campaign.TierIds[1]);

        await using var owner = new Npgsql.NpgsqlConnection(database.Owner);
        await owner.OpenAsync(Ct);
        await using var locking = await owner.BeginTransactionAsync(Ct);
        await using (var lockLedger = new Npgsql.NpgsqlCommand("LOCK TABLE pledges.pledge_ledger IN SHARE MODE", owner, locking))
        {
            await lockLedger.ExecuteNonQueryAsync(Ct);
        }

        var change = Change(backer, pledge, 6_000, campaign.TierIds[0]);
        await Task.Delay(TimeSpan.FromMilliseconds(800), Ct);
        await locking.CommitAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await change).StatusCode);
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task The_total_equals_the_ledger_sum_after_any_sequence()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 2), ("Founder", 9_900, null));
        var (anna, ben, cleo) = (given.Backer(Guid.NewGuid()), given.Backer(Guid.NewGuid()), given.Backer(Guid.NewGuid()));
        var a = await Place(anna, campaign, 6_000, campaign.TierIds[0]);
        var b = await Place(ben, campaign, 4_900, campaign.TierIds[0]);
        var c = await Place(cleo, campaign, 10_000);

        Assert.Equal(HttpStatusCode.OK, (await Change(anna, a, 10_000, campaign.TierIds[1])).StatusCode);   // up, and to another tier
        Assert.Equal(HttpStatusCode.OK, (await Change(cleo, c, 2_500)).StatusCode);                         // down
        Assert.Equal(HttpStatusCode.OK, (await ben.DeleteAsync(b, Ct)).StatusCode);                          // out
        Assert.Equal(HttpStatusCode.OK, (await Change(cleo, c, 4_900, campaign.TierIds[0])).StatusCode);   // into the place Anna and Ben freed

        await AssertTotalIsTheLedger(given, campaign.CampaignId);
        Assert.Equal(14_900L, await given.Scalar<long>("SELECT coalesce(sum(delta_minor), 0)::bigint FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId));
        Assert.Equal(1, await given.Scalar<int>("SELECT claimed FROM pledges.tier_counters WHERE campaign_id = @id AND limit_qty = 2", campaign.CampaignId));
        Assert.Equal(7L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE campaign_id = @id", campaign.CampaignId)); // 3 activations, 3 changes, 1 cancel
    }

    [Fact]
    public async Task Nothing_changes_after_the_deadline()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);
        await given.Execute("UPDATE pledges.funding_windows SET deadline = now() - interval '1 millisecond' WHERE campaign_id = @id", campaign.CampaignId);

        var changed = await Change(backer, pledge, 9_000);
        var cancelled = await backer.DeleteAsync(pledge, Ct);

        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("campaign_closed", await Reason(changed));
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal("campaign_closed", await Reason(cancelled));
        Assert.Equal("ACTIVE", (await backer.GetFromJsonAsync<JsonElement>(pledge, Ct)).GetProperty("status").GetString());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task A_move_to_a_sold_out_tier_changes_nothing()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish(5_000_000, ("Early Bird", 4_900, 1), ("Standard copy", 5_900, null));
        await Place(given.Backer(Guid.NewGuid()), campaign, 4_900, campaign.TierIds[0]);
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000, campaign.TierIds[1]);

        var refused = await Change(backer, pledge, 7_000, campaign.TierIds[0]);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("tier_sold_out", await Reason(refused));
        Assert.Equal(6_000, (await backer.GetFromJsonAsync<JsonElement>(pledge, Ct)).GetProperty("amount_minor").GetInt64());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task After_a_cancel_the_backer_may_pledge_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var first = await Place(backer, campaign, 6_000);
        (await backer.DeleteAsync(first, Ct)).EnsureSuccessStatusCode();

        await Place(backer, campaign, 7_000);

        Assert.Equal("CANCELLED", (await backer.GetFromJsonAsync<JsonElement>(first, Ct)).GetProperty("status").GetString());
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task Another_backer_can_neither_change_nor_cancel_my_pledge()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var pledge = await Place(given.Backer(Guid.NewGuid()), campaign, 6_000);
        var someoneElse = given.Backer(Guid.NewGuid());

        var changed = await Change(someoneElse, pledge, 9_000);
        var cancelled = await someoneElse.DeleteAsync(pledge, Ct);

        Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
        var pledgeId = Guid.ParseExact(pledge["/v1/pledges/plg_".Length..], "N");
        Assert.Equal("ACTIVE", await given.Scalar<string>("SELECT status FROM pledges.pledges WHERE id = @id", pledgeId));
        Assert.Equal(6_000L, await given.Scalar<long>("SELECT amount_minor FROM pledges.pledges WHERE id = @id", pledgeId));
        Assert.Equal(1L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE pledge_id = @id", pledgeId));
    }

    [Fact]
    public async Task A_cancelled_pledge_cannot_be_changed_or_cancelled_again()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);
        (await backer.DeleteAsync(pledge, Ct)).EnsureSuccessStatusCode();

        var again = await backer.DeleteAsync(pledge, Ct);
        var changed = await Change(backer, pledge, 9_000);

        Assert.Equal("pledge_not_active", await Reason(again));
        Assert.Equal("pledge_not_active", await Reason(changed));
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }

    [Fact]
    public async Task Concurrent_changes_of_one_pledge_keep_the_total_exact()
    {
        var given = new Scenario(database, _host);
        var campaign = await given.Publish();
        var backer = given.Backer(Guid.NewGuid());
        var pledge = await Place(backer, campaign, 6_000);

        var amounts = Enumerable.Range(1, 10).Select(i => 6_000L + (i * 100)).ToArray();
        var responses = await Task.WhenAll(amounts.Select(a => ChangeUntilAnswered(backer, pledge, a)));

        // Each change waited for the one before on the pledge's row lock, then applied in full.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var pledgeId = Guid.ParseExact(pledge["/v1/pledges/plg_".Length..], "N");
        Assert.Equal(10L, await given.Scalar<long>("SELECT count(*) FROM pledges.pledge_ledger WHERE pledge_id = @id AND kind = 'CHANGE'", pledgeId));
        var final = await given.Scalar<long>("SELECT amount_minor FROM pledges.pledges WHERE id = @id", pledgeId);
        Assert.Contains(final, amounts);
        Assert.Equal(final, await given.Scalar<long>("SELECT sum(delta_minor)::bigint FROM pledges.pledge_ledger WHERE pledge_id = @id", pledgeId));
        await AssertTotalIsTheLedger(given, campaign.CampaignId);
    }
}
